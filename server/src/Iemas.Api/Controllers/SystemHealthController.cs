using Hangfire;
using Hangfire.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Iemas.Api.Controllers;

public record HealthCheckEntryDto(string Name, string Status, string? Description, double DurationMs, IReadOnlyDictionary<string, object>? Data);
public record SystemHealthDto(string Status, double TotalDurationMs, List<HealthCheckEntryDto> Checks, int? FailedJobCount, int? ScheduledJobCount, int? ProcessingJobCount);

/// <summary>
/// Requirements §106 (System Health: database, email provider, AI provider, background jobs,
/// queue depth, failed jobs — health states HEALTHY/WARNING/FAILED), §86 (SYSTEM nav → System
/// Health), §85 (RBAC — this exposes the same dependency detail as the unauthenticated
/// /health/ready infra endpoint, but that endpoint is deliberately open for Docker/orchestrator
/// probes without a token; this controller is the CMS-facing, authenticated equivalent so the
/// same information doesn't require bypassing normal RBAC to view in the UI). Reuses the exact
/// same registered IHealthCheckService/checks as /health/ready — never a second, parallel
/// implementation of "is Postgres/IMAP/OpenRouter/Hangfire OK."
/// </summary>
[ApiController]
[Route("api/v1/system-health")]
[Authorize(Policy = "RequireAuditorOrAbove")]
public class SystemHealthController : ControllerBase
{
    private readonly HealthCheckService _healthCheckService;

    public SystemHealthController(HealthCheckService healthCheckService)
    {
        _healthCheckService = healthCheckService;
    }

    [HttpGet]
    public async Task<ActionResult<SystemHealthDto>> Get(CancellationToken cancellationToken)
    {
        var report = await _healthCheckService.CheckHealthAsync(check => check.Tags.Contains("ready"), cancellationToken);

        int? failedJobs = null, scheduledJobs = null, processingJobs = null;
        try
        {
            // §106 "background jobs, queue depth, failed jobs" — Hangfire's own monitoring API,
            // best-effort: a Hangfire storage hiccup here must not make this whole endpoint fail,
            // since the health-check report above is already the authoritative dependency status.
            var monitor = JobStorage.Current.GetMonitoringApi();
            failedJobs = (int)monitor.FailedCount();
            scheduledJobs = (int)monitor.ScheduledCount();
            processingJobs = (int)monitor.ProcessingCount();
        }
        catch
        {
            // Left null — the CMS shows "unavailable" rather than a stale/wrong number.
        }

        return Ok(new SystemHealthDto(
            report.Status.ToString(),
            report.TotalDuration.TotalMilliseconds,
            report.Entries.Select(e => new HealthCheckEntryDto(
                e.Key, e.Value.Status.ToString(), e.Value.Description, e.Value.Duration.TotalMilliseconds,
                e.Value.Data.Count > 0 ? e.Value.Data : null)).ToList(),
            failedJobs, scheduledJobs, processingJobs));
    }
}
