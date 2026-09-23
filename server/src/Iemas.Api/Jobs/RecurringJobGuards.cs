using Hangfire;
using Iemas.Application.Escalations;
using Iemas.Application.Reminders;

namespace Iemas.Api.Jobs;

/// <summary>
/// Phase 10 hardening: thin wrappers so <see cref="DisableConcurrentExecutionAttribute"/> can sit on
/// the exact method Hangfire invokes for the Reminder and Escalation recurring jobs. The attribute
/// only works via Hangfire's own job filter pipeline, which requires a Hangfire package reference —
/// something <c>Iemas.Application</c> deliberately does not take (Application defines interfaces
/// only; Infrastructure/Api are the only layers allowed a framework dependency), so the guard lives
/// here instead of directly on <see cref="ReminderExecutionService.RunAsync"/>/
/// <see cref="EscalationService.RunAsync"/>.
///
/// Each service's own row-level idempotency (Reminder's <c>ExecutionClaimToken</c> unique-index race,
/// Escalation's per-level Executed-row check) already prevents a *duplicate send/escalation* even
/// under concurrent execution — this attribute is defense-in-depth against a slower failure mode:
/// an overrunning run (e.g. a slow mailbox or DB) still being mid-batch when its own next cron tick
/// fires, which would otherwise mean two instances racing over the same candidate set, most of that
/// work thrown away, and doubled DB load during exactly the conditions (a struggling dependency)
/// where that load is least wanted. A distributed Hangfire lock (backed by PostgreSQL storage, so it
/// works correctly even if the overlapping tick lands on a different server process) simply skips the
/// second invocation instead.
/// </summary>
public class RecurringJobGuards
{
    private readonly ReminderExecutionService _reminderExecutionService;
    private readonly EscalationService _escalationService;

    public RecurringJobGuards(ReminderExecutionService reminderExecutionService, EscalationService escalationService)
    {
        _reminderExecutionService = reminderExecutionService;
        _escalationService = escalationService;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public Task RunRemindersAsync(int batchSize, CancellationToken cancellationToken) =>
        _reminderExecutionService.RunAsync(batchSize, cancellationToken);

    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public Task RunEscalationsAsync(int batchSize, CancellationToken cancellationToken) =>
        _escalationService.RunAsync(batchSize, cancellationToken);
}
