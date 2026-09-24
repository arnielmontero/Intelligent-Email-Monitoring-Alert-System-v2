using System.Net.Http;
using System.Text.Json;
using Iemas.Agent.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace Iemas.Agent.Services;

public enum AgentConnectionState { Connecting, Connected, Disconnected }

/// <summary>
/// Requirements §8.1 (maintain SignalR connection, heartbeat, sync after reconnect), §75/§77
/// (server is authoritative; the Agent rebuilds its UI from a fresh SYNC on every (re)connect),
/// §76 (SignalR is a transport, never the only path — see IemasApiClient's REST fallbacks for
/// case-actions/comments/heartbeat, which this class does not replace). Connects to the exact hub
/// path the server maps: /hubs/agent, authenticated via the AgentScheme bearer token passed as the
/// access_token query parameter (the only way a SignalR WebSocket handshake can carry a header-like
/// credential — see server Program.cs's OnMessageReceived wiring).
/// </summary>
public class SignalRAgentConnection : IAsyncDisposable
{
    private HubConnection? _connection;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public event Action<AgentConnectionState>? StateChanged;
    public event Action<AgentPushMessage>? CommandReceived;

    public AgentConnectionState State { get; private set; } = AgentConnectionState.Disconnected;

    public async Task ConnectAsync(string serverBaseUrl, string accessToken, bool allowInsecureTls, CancellationToken ct)
    {
        await DisposeConnectionAsync();

        var hubUrl = serverBaseUrl.TrimEnd('/') + "/hubs/agent";

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
                if (allowInsecureTls)
                {
                    options.HttpMessageHandlerFactory = handler =>
                    {
                        if (handler is HttpClientHandler httpHandler)
                        {
                            httpHandler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
                        }
                        return handler;
                    };
                }
            })
            .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30) })
            .Build();

        // §72 — the one and only server-to-agent push method name; payload is always the closed
        // AgentPushMessage shape (see AgentNotificationDispatcher server-side), never arbitrary.
        _connection.On<JsonElement>("AgentCommand", OnAgentCommand);

        _connection.Reconnecting += _ =>
        {
            SetState(AgentConnectionState.Connecting);
            return Task.CompletedTask;
        };

        _connection.Reconnected += _ =>
        {
            // §75 SYNC on every reconnect — handled by the caller (AgentSessionManager) which owns
            // the actual Sync() REST/hub call and UI rebuild; this event only flips connection state.
            SetState(AgentConnectionState.Connected);
            return Task.CompletedTask;
        };

        _connection.Closed += _ =>
        {
            SetState(AgentConnectionState.Disconnected);
            return Task.CompletedTask;
        };

        SetState(AgentConnectionState.Connecting);
        await _connection.StartAsync(ct);
        SetState(AgentConnectionState.Connected);
    }

    private void OnAgentCommand(JsonElement payload)
    {
        try
        {
            var message = JsonSerializer.Deserialize<AgentPushMessage>(payload.GetRawText(), JsonOptions);
            if (message is not null)
            {
                CommandReceived?.Invoke(message);
            }
        }
        catch (JsonException)
        {
            // A malformed push must never crash the Agent — §8.2 "no arbitrary remote command
            // execution": an unparseable payload is simply dropped, not acted on.
        }
    }

    /// <summary>§73 HEARTBEAT over the hub (in addition to the REST fallback).</summary>
    public async Task SendHeartbeatAsync(string? agentVersion, CancellationToken ct)
    {
        if (_connection is { State: HubConnectionState.Connected })
        {
            await _connection.InvokeAsync("Heartbeat", agentVersion, ct);
        }
    }

    /// <summary>§73 SYNC over the hub — returns the same AgentSyncResponse shape as the REST endpoint.</summary>
    public async Task<AgentSyncResponse?> RequestSyncAsync(CancellationToken ct)
    {
        if (_connection is { State: HubConnectionState.Connected })
        {
            return await _connection.InvokeAsync<AgentSyncResponse>("Sync", ct);
        }
        return null;
    }

    private void SetState(AgentConnectionState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    private async Task DisposeConnectionAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeConnectionAsync();
    }
}
