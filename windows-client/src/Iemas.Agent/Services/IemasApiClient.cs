using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Text.Json;
using Iemas.Agent.Models;

namespace Iemas.Agent.Services;

/// <summary>
/// Requirements §68/§73/§75 — the Agent's REST surface against the server's actual contract
/// (AgentEnrollmentController, AgentAuthController, AgentOperationsController). Every call here
/// matches an endpoint verified to exist by reading those controllers directly, not guessed.
/// §8.2 — never sends a mailbox password/API key; the only credential ever attached is the
/// server-issued bearer access token or, during the one-shot registration flow, the server-issued
/// Registration Key it just received.
/// </summary>
public class IemasApiClient : IDisposable
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string? AccessToken { get; set; }

    public IemasApiClient(string serverBaseUrl, bool allowInsecureTls)
    {
        var handler = new HttpClientHandler();
        if (allowInsecureTls)
        {
            // Dev/self-signed-cert convenience only — an explicit opt-in in Settings, never the
            // silent default (§84 HTTPS is a minimum security requirement).
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }

        _http = new HttpClient(handler) { BaseAddress = new Uri(serverBaseUrl.TrimEnd('/') + "/") };
    }

    private void ApplyAuth(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(AccessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        }
    }

    public Task<ApiResult<RegisterAgentResponse>> RegisterAsync(RegisterAgentRequest request, CancellationToken ct) =>
        ExecuteAsync<RegisterAgentResponse>(async () =>
        {
            using var response = await _http.PostAsJsonAsync("api/v1/agent-enrollment/register", request, JsonOptions, ct);
            return await ReadResultAsync<RegisterAgentResponse>(response, ct);
        });

    public Task<ApiResult<AgentRegistrationStatusResponse>> GetRegistrationStatusAsync(string registrationRequestToken, CancellationToken ct) =>
        ExecuteAsync<AgentRegistrationStatusResponse>(async () =>
        {
            using var response = await _http.GetAsync($"api/v1/agent-enrollment/status/{Uri.EscapeDataString(registrationRequestToken)}", ct);
            return await ReadResultAsync<AgentRegistrationStatusResponse>(response, ct);
        });

    public Task<ApiResult<AgentAuthenticateResponse>> AuthenticateAsync(Guid agentId, string registrationKey, CancellationToken ct) =>
        ExecuteAsync<AgentAuthenticateResponse>(async () =>
        {
            using var response = await _http.PostAsJsonAsync("api/v1/agent-auth/authenticate", new AgentAuthenticateRequest(agentId, registrationKey), JsonOptions, ct);
            return await ReadResultAsync<AgentAuthenticateResponse>(response, ct);
        });

    public Task<ApiResult<AgentSyncResponse>> SyncAsync(CancellationToken ct) =>
        ExecuteAsync<AgentSyncResponse>(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/agent/sync");
            ApplyAuth(request);
            using var response = await _http.SendAsync(request, ct);
            return await ReadResultAsync<AgentSyncResponse>(response, ct);
        });

    public Task<ApiResult<bool>> HeartbeatAsync(string? agentVersion, CancellationToken ct) =>
        ExecuteAsync<bool>(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/agent/heartbeat")
            {
                Content = JsonContent.Create(new HeartbeatRequest(agentVersion), options: JsonOptions),
            };
            ApplyAuth(request);
            using var response = await _http.SendAsync(request, ct);
            return await ReadResultAsync<bool>(response, ct, treatNoContentAsSuccess: true);
        });

    public Task<ApiResult<CaseActionResultDto>> SubmitCaseActionAsync(SubmitCaseActionRequest request, CancellationToken ct) =>
        ExecuteAsync<CaseActionResultDto>(async () =>
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/v1/agent/case-actions")
            {
                Content = JsonContent.Create(request, options: JsonOptions),
            };
            ApplyAuth(httpRequest);
            using var response = await _http.SendAsync(httpRequest, ct);
            return await ReadResultAsync<CaseActionResultDto>(response, ct);
        });

    public Task<ApiResult<bool>> SubmitCaseCommentAsync(SubmitCaseCommentRequest request, CancellationToken ct) =>
        ExecuteAsync<bool>(async () =>
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/v1/agent/case-comments")
            {
                Content = JsonContent.Create(request, options: JsonOptions),
            };
            ApplyAuth(httpRequest);
            using var response = await _http.SendAsync(httpRequest, ct);
            return await ReadResultAsync<bool>(response, ct, treatNoContentAsSuccess: true);
        });

    public Task<ApiResult<bool>> CompleteCaseAsync(Guid caseId, CompleteCaseActionRequest request, CancellationToken ct) =>
        ExecuteAsync<bool>(async () =>
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"api/v1/agent/cases/{caseId}/complete")
            {
                Content = JsonContent.Create(request, options: JsonOptions),
            };
            ApplyAuth(httpRequest);
            using var response = await _http.SendAsync(httpRequest, ct);
            return await ReadResultAsync<bool>(response, ct, treatNoContentAsSuccess: true);
        });

    /// <summary>§8.1 "Report errors."</summary>
    public async Task ReportErrorAsync(string detail, CancellationToken ct)
    {
        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/v1/agent/errors")
            {
                Content = JsonContent.Create(detail, options: JsonOptions),
            };
            ApplyAuth(httpRequest);
            using var response = await _http.SendAsync(httpRequest, ct);
        }
        catch
        {
            // Best-effort — a failed error report must never itself crash the Agent.
        }
    }

    /// <summary>
    /// Every network call goes through here. A WPF async-void event handler (e.g. a button Click)
    /// that lets an exception escape crashes the ENTIRE application — confirmed live during E2E
    /// verification (an unhandled TLS/connectivity exception from RegisterAsync took the whole
    /// process down). §8.1 "Report errors" requires the Agent to survive and report a failure, not
    /// die; this is the single choke point that guarantees every API call surfaces as a graceful
    /// ApiResult.Fail instead of a thrown exception, regardless of which specific method is called.
    /// </summary>
    private static async Task<ApiResult<T>> ExecuteAsync<T>(Func<Task<ApiResult<T>>> action)
    {
        try
        {
            return await action();
        }
        catch (HttpRequestException ex)
        {
            return ApiResult<T>.Fail(0, $"Connection failed: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return ApiResult<T>.Fail(0, "Request timed out.");
        }
        catch (Exception ex)
        {
            return ApiResult<T>.Fail(0, $"Unexpected error: {ex.Message}");
        }
    }

    private static async Task<ApiResult<T>> ReadResultAsync<T>(HttpResponseMessage response, CancellationToken ct, bool treatNoContentAsSuccess = false)
    {
        if (response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent || treatNoContentAsSuccess && response.Content.Headers.ContentLength is 0 or null)
            {
                return ApiResult<T>.Ok(default!);
            }

            try
            {
                var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
                return ApiResult<T>.Ok(value!);
            }
            catch (JsonException)
            {
                return ApiResult<T>.Ok(default!);
            }
        }

        string? message = null;
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>(JsonOptions, ct);
            message = error?.Message;
        }
        catch { /* body wasn't JSON — fall through to status-based message */ }

        return ApiResult<T>.Fail((int)response.StatusCode, message ?? response.ReasonPhrase ?? "Request failed.");
    }

    public void Dispose() => _http.Dispose();
}

public record ApiResult<T>(bool Succeeded, T? Value, int StatusCode, string? Error)
{
    public static ApiResult<T> Ok(T value) => new(true, value, 200, null);
    public static ApiResult<T> Fail(int statusCode, string error) => new(false, default, statusCode, error);
}
