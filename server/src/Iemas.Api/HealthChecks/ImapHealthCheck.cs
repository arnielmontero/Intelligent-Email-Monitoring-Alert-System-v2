using Iemas.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Iemas.Api.HealthChecks;

// Observability (Phase 10 hardening): reports IMAP health from the *real, already-collected*
// result of the last actual intake run against each monitored mailbox (EmailSyncState, written by
// EmailIntakeService on every job tick), rather than opening a fresh IMAP connection on every poll
// of this endpoint. A health check must not itself cause production work (an extra connection per
// account, per poll, from whatever's hitting /health) — the real connectivity test already happens
// on its own schedule via the recurring Email Intake job; this only reports on it.
public sealed class ImapHealthCheck : IHealthCheck
{
    // A mailbox is considered stale (not just "had one bad tick") only after this many consecutive
    // failures — one transient blip (already retried internally, per Phase 10's IMAP retry/backoff
    // work) should not flip the whole health check to Unhealthy.
    private const int ConsecutiveFailureThresholdForUnhealthy = 3;

    private readonly IAppDbContext _dbContext;

    public ImapHealthCheck(IAppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var states = await _dbContext.EmailSyncStates
            .Include(s => s.EmailAccount)
            .Where(s => s.EmailAccount.MonitoringEnabled && s.EmailAccount.IsActive)
            .ToListAsync(cancellationToken);

        if (states.Count == 0)
        {
            return HealthCheckResult.Healthy("No monitored mailboxes configured.");
        }

        var unhealthyAccounts = states
            .Where(s => s.ConsecutiveFailureCount >= ConsecutiveFailureThresholdForUnhealthy)
            .Select(s => $"{s.EmailAccount.EmailAddress} ({s.ConsecutiveFailureCount} consecutive failures; last error: {s.LastSyncError})")
            .ToList();

        var data = new Dictionary<string, object>
        {
            ["monitoredAccounts"] = states.Count,
            ["accountsWithFailures"] = states.Count(s => s.ConsecutiveFailureCount > 0),
        };

        if (unhealthyAccounts.Count > 0)
        {
            return HealthCheckResult.Unhealthy(
                $"{unhealthyAccounts.Count} of {states.Count} monitored mailbox(es) have {ConsecutiveFailureThresholdForUnhealthy}+ consecutive sync failures: {string.Join("; ", unhealthyAccounts)}",
                data: data);
        }

        var accountsWithAnyFailure = states.Count(s => s.ConsecutiveFailureCount > 0);
        if (accountsWithAnyFailure > 0)
        {
            return HealthCheckResult.Degraded(
                $"{accountsWithAnyFailure} of {states.Count} monitored mailbox(es) have recent (but not yet threshold-breaching) sync failures.",
                data: data);
        }

        return HealthCheckResult.Healthy($"All {states.Count} monitored mailbox(es) synced successfully on their last run.", data);
    }
}
