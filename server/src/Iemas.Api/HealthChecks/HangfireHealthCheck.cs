using Hangfire;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Iemas.Api.HealthChecks;

// Observability (Phase 10 hardening): checks Hangfire storage connectivity and server/worker
// presence via IMonitoringApi — a lightweight read against Hangfire's own PostgreSQL-backed
// storage tables, not a real job dispatch. Never enqueues a job merely to prove the system works;
// that would itself be production work triggered by a health poll.
public sealed class HangfireHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var monitoringApi = JobStorage.Current.GetMonitoringApi();
            var servers = monitoringApi.Servers();

            var data = new Dictionary<string, object>
            {
                ["serverCount"] = servers.Count,
                ["totalWorkerCount"] = servers.Sum(s => s.WorkersCount),
                ["enqueuedCount"] = monitoringApi.EnqueuedCount("default"),
                ["failedCount"] = monitoringApi.FailedCount(),
            };

            if (servers.Count == 0)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    "Hangfire storage is reachable, but no server is currently registered — recurring jobs will not run.",
                    data: data));
            }

            if (servers.All(s => s.WorkersCount == 0))
            {
                return Task.FromResult(HealthCheckResult.Degraded(
                    "Hangfire server(s) registered, but none report any available workers.",
                    data: data));
            }

            return Task.FromResult(HealthCheckResult.Healthy(
                $"{servers.Count} Hangfire server(s) registered with {data["totalWorkerCount"]} total worker(s).",
                data));
        }
        catch (Exception ex)
        {
            // Storage (PostgreSQL) unreachable, or Hangfire not yet initialized — either way, a
            // genuine failure to report, not a value to guess at.
            return Task.FromResult(HealthCheckResult.Unhealthy("Failed to query Hangfire storage.", ex));
        }
    }
}
