using System.Diagnostics;
using System.Reflection;
using System.Windows.Threading;
using Iemas.Agent.Models;

namespace Iemas.Agent.Services;

public enum SessionState { NotRegistered, PendingApproval, Rejected, Authenticating, Connected, Disconnected, Error }

/// <summary>
/// Requirements §68 (full registration → CONNECTED state machine), §8.1 (heartbeat, sync after
/// reconnect, report version/errors), §75/§77 (server is authoritative; UI is rebuilt from SYNC).
/// This is the single owner of the Agent's identity/connection lifecycle — Views only observe its
/// events and call its action methods; no view talks to IemasApiClient/SignalRAgentConnection
/// directly, keeping exactly one place responsible for the state machine's correctness.
/// </summary>
public class AgentSessionManager : IAsyncDisposable
{
    private readonly CredentialStore _credentialStore = new();
    private readonly AgentSettings _settings;
    private IemasApiClient _api;
    private readonly SignalRAgentConnection _signalR = new();
    private readonly DispatcherTimer _heartbeatTimer;
    private readonly DispatcherTimer _reauthWatchTimer;

    private Guid _agentId;
    private string? _registrationKey;
    private DateTimeOffset _tokenExpiresAt;
    private string _registrationRequestToken = string.Empty;

    public static string AgentVersion { get; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public SessionState State { get; private set; } = SessionState.NotRegistered;
    public string EmployeeName { get; private set; } = string.Empty;
    public List<CaseDto> ActionRequired { get; private set; } = new();
    public List<CaseDto> Waiting { get; private set; } = new();

    public event Action? StateUpdated;
    public event Action<AgentPushMessage>? PushReceived;

    public AgentSessionManager()
    {
        _settings = AgentSettings.Load();
        _api = new IemasApiClient(_settings.ServerUrl, _settings.AllowInsecureTls);

        _signalR.StateChanged += OnSignalRStateChanged;
        _signalR.CommandReceived += OnPushReceived;

        _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _heartbeatTimer.Tick += async (_, _) => await SafeHeartbeatAsync();

        // §68 registration is a polling flow ("Administrator Reviews" happens on the CMS side, on
        // the admin's own schedule) — poll every few seconds while PendingApproval so the Agent
        // reaches CONNECTED automatically the moment an admin approves, without a manual retry.
        _reauthWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _reauthWatchTimer.Tick += async (_, _) => await PollRegistrationIfPendingAsync();
    }

    public async Task StartAsync()
    {
        var stored = _credentialStore.Load();
        if (stored is null)
        {
            State = SessionState.NotRegistered;
            StateUpdated?.Invoke();
            return;
        }

        _agentId = stored.AgentId;
        _registrationKey = stored.RegistrationKey;
        RebuildApiClientIfServerChanged(stored.ServerUrl);
        await AuthenticateAndConnectAsync();
    }

    /// <summary>§68 "Agent Sends Registration Request → Server Creates PENDING."</summary>
    public async Task<(bool Succeeded, string? Error)> RegisterAsync(string emailAddress, string clientName)
    {
        var request = new RegisterAgentRequest(emailAddress, clientName, _settings.ServerUrl, AgentVersion, Environment.MachineName);
        var result = await _api.RegisterAsync(request, CancellationToken.None);
        if (!result.Succeeded || result.Value is null)
        {
            return (false, result.Error);
        }

        _agentId = result.Value.AgentId;
        _registrationRequestToken = result.Value.RegistrationRequestToken;
        State = SessionState.PendingApproval;
        StateUpdated?.Invoke();
        _reauthWatchTimer.Start();
        return (true, null);
    }

    private async Task PollRegistrationIfPendingAsync()
    {
        if (State != SessionState.PendingApproval || string.IsNullOrEmpty(_registrationRequestToken))
        {
            return;
        }

        var result = await _api.GetRegistrationStatusAsync(_registrationRequestToken, CancellationToken.None);
        if (!result.Succeeded || result.Value is null)
        {
            return;
        }

        // §68 status: 0=Pending, 1=Approved, 2=Rejected, 3=Revoked (AgentRegistrationStatus order).
        switch (result.Value.Status)
        {
            case 1 when result.Value.RegistrationKey is not null:
                _reauthWatchTimer.Stop();
                _registrationKey = result.Value.RegistrationKey;
                _credentialStore.Save(_agentId, _registrationKey, _settings.ServerUrl);
                await AuthenticateAndConnectAsync();
                break;
            case 2:
                _reauthWatchTimer.Stop();
                State = SessionState.Rejected;
                StateUpdated?.Invoke();
                break;
        }
    }

    private async Task AuthenticateAndConnectAsync()
    {
        if (_registrationKey is null) return;

        State = SessionState.Authenticating;
        StateUpdated?.Invoke();

        var authResult = await _api.AuthenticateAsync(_agentId, _registrationKey, CancellationToken.None);
        if (!authResult.Succeeded || authResult.Value is null)
        {
            // §69/§70 — an invalid/revoked credential must not be treated as "try again later";
            // clear it so the user re-registers rather than looping forever on a dead credential.
            _credentialStore.Clear();
            State = SessionState.Error;
            StateUpdated?.Invoke();
            return;
        }

        _api.AccessToken = authResult.Value.AccessToken;
        _tokenExpiresAt = authResult.Value.ExpiresAt;
        EmployeeName = authResult.Value.EmployeeName;

        try
        {
            await _signalR.ConnectAsync(_settings.ServerUrl, authResult.Value.AccessToken, _settings.AllowInsecureTls, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await _api.ReportErrorAsync($"SignalR connect failed: {ex.Message}", CancellationToken.None);
        }

        await RefreshSyncAsync();
        _heartbeatTimer.Start();
    }

    private void OnSignalRStateChanged(AgentConnectionState state)
    {
        State = state switch
        {
            AgentConnectionState.Connected => SessionState.Connected,
            AgentConnectionState.Connecting => SessionState.Authenticating,
            _ => SessionState.Disconnected,
        };
        StateUpdated?.Invoke();

        if (state == AgentConnectionState.Connected)
        {
            // §75 — SYNC is mandatory on every (re)connect; server state, not whatever the Agent
            // cached before the drop, is what the UI shows next.
            _ = RefreshSyncAsync();
        }
    }

    private void OnPushReceived(AgentPushMessage message)
    {
        PushReceived?.Invoke(message);

        // §72/§104 — a push is a hint to refresh, never itself the state; always re-pull the
        // authoritative list rather than mutating ActionRequired/Waiting from the push payload alone.
        if (message.Type is "SHOW_CASE" or "SHOW_REMINDER" or "SHOW_NOTIFICATION" or "SYNC_REQUIRED")
        {
            _ = RefreshSyncAsync();
        }

        if (message.Type is "SHOW_CASE" or "SHOW_REMINDER" or "SHOW_NOTIFICATION")
        {
            var toastError = ToastService.Show(message.Title ?? "IEMAS", message.Message ?? string.Empty, message.CaseId);
            if (toastError is not null)
            {
                _ = _api.ReportErrorAsync($"Toast display failed for {message.Type}: {toastError}", CancellationToken.None);
            }
        }
    }

    public async Task RefreshSyncAsync()
    {
        var result = await _api.SyncAsync(CancellationToken.None);
        if (result.Succeeded && result.Value is not null)
        {
            ActionRequired = result.Value.ActionRequired;
            Waiting = result.Value.Waiting;
            StateUpdated?.Invoke();
        }
    }

    private async Task SafeHeartbeatAsync()
    {
        try
        {
            // §69 15-minute Agent token lifetime, no refresh token — re-authenticate proactively
            // before expiry rather than waiting for a 401, so the SignalR connection (which used the
            // token at handshake time) doesn't silently start rejecting hub calls mid-session.
            if (DateTimeOffset.UtcNow >= _tokenExpiresAt.AddMinutes(-2) && _registrationKey is not null)
            {
                await AuthenticateAndConnectAsync();
                return;
            }

            await _api.HeartbeatAsync(AgentVersion, CancellationToken.None);
            await _signalR.SendHeartbeatAsync(AgentVersion, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await _api.ReportErrorAsync($"Heartbeat failed: {ex.Message}", CancellationToken.None);
        }
    }

    public async Task<ApiResult<CaseActionResultDto>> SubmitActionAsync(Guid caseId, CaseActionType actionType, string? comment, DateTimeOffset? requestedForUtc = null)
    {
        var request = new SubmitCaseActionRequest(Guid.NewGuid().ToString("N"), caseId, actionType, comment, DateTimeOffset.UtcNow, requestedForUtc);
        var result = await _api.SubmitCaseActionAsync(request, CancellationToken.None);
        if (result.Succeeded)
        {
            await RefreshSyncAsync();
        }
        return result;
    }

    public async Task<ApiResult<bool>> SubmitCommentAsync(Guid caseId, string comment)
    {
        var request = new SubmitCaseCommentRequest(Guid.NewGuid().ToString("N"), caseId, comment, DateTimeOffset.UtcNow);
        return await _api.SubmitCaseCommentAsync(request, CancellationToken.None);
    }

    public async Task<ApiResult<bool>> CompleteCaseAsync(Guid caseId, CaseCompletionReason reason, string? comment)
    {
        var result = await _api.CompleteCaseAsync(caseId, new CompleteCaseActionRequest(reason, comment), CancellationToken.None);
        if (result.Succeeded)
        {
            await RefreshSyncAsync();
        }
        return result;
    }

    /// <summary>§8.1 "Report errors" — exposed for the app-wide unhandled-exception handler (see App.xaml.cs), best-effort, never throws.</summary>
    public Task ReportErrorAsync(string detail) => _api.ReportErrorAsync(detail, CancellationToken.None);

    /// <summary>§8.1 "Open the normal email client/webmail when requested" — never IEMAS itself replying, just handing off to the OS default mail handler / the employee's own webmail.</summary>
    public static void OpenEmailClient(string? webmailUrl = null)
    {
        var target = string.IsNullOrWhiteSpace(webmailUrl) ? "mailto:" : webmailUrl;
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    public void ApplyServerUrlChange(string newServerUrl, bool allowInsecureTls)
    {
        _settings.ServerUrl = newServerUrl;
        _settings.AllowInsecureTls = allowInsecureTls;
        _settings.Save();
        RebuildApiClientIfServerChanged(newServerUrl);
    }

    private void RebuildApiClientIfServerChanged(string serverUrl)
    {
        _api.Dispose();
        _api = new IemasApiClient(serverUrl, _settings.AllowInsecureTls);
    }

    public void SignOut()
    {
        _heartbeatTimer.Stop();
        _reauthWatchTimer.Stop();
        _credentialStore.Clear();
        State = SessionState.NotRegistered;
        StateUpdated?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _heartbeatTimer.Stop();
        _reauthWatchTimer.Stop();
        await _signalR.DisposeAsync();
        _api.Dispose();
    }
}
