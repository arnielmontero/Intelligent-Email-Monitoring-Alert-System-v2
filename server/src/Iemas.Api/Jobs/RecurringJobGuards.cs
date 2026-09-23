using Hangfire;
using Iemas.Application.EmailClassification;
using Iemas.Application.EmailIntake;
using Iemas.Application.Escalations;
using Iemas.Application.Reminders;

namespace Iemas.Api.Jobs;

/// <summary>
/// Phase 10 hardening: thin wrappers so <see cref="DisableConcurrentExecutionAttribute"/> can sit on
/// the exact method Hangfire invokes for the Reminder, Escalation, Email Intake, and AI
/// Classification recurring jobs. The attribute only works via Hangfire's own job filter pipeline,
/// which requires a Hangfire package reference — something <c>Iemas.Application</c> deliberately
/// does not take (Application defines interfaces only; Infrastructure/Api are the only layers
/// allowed a framework dependency), so the guard lives here instead of directly on
/// <see cref="ReminderExecutionService.RunAsync"/>/<see cref="EscalationService.RunAsync"/>/
/// <see cref="EmailIntakeService.RunAllAsync"/>/<see cref="EmailClassificationService.RunAsync"/>.
///
/// Each service's own row-level idempotency (Reminder's <c>ExecutionClaimToken</c> unique-index race,
/// Escalation's per-level Executed-row check, Intake's UID-watermark) already prevents a *duplicate
/// send/escalation/message* even under concurrent execution — this attribute is defense-in-depth
/// against a slower failure mode: an overrunning run (e.g. a slow mailbox or DB) still being
/// mid-batch when its own next cron tick fires, which would otherwise mean two instances racing
/// over the same candidate set, most of that work thrown away, and doubled DB/IMAP/AI-provider load
/// during exactly the conditions (a struggling dependency) where that load is least wanted. Email
/// Intake and AI Classification are the tightest interval of any job here (every 2 minutes), so they
/// are most likely to genuinely overlap their own next tick under a slow/degraded dependency.
///
/// For AI Classification specifically, this guard is not just an efficiency concern: the circuit
/// breaker's (<c>AiCircuitBreakerStore</c>) HALF-OPEN state grants exactly one probe attempt per
/// cooldown, and while the store's own per-entry locking is the actual correctness guarantee for
/// that invariant even under genuinely concurrent callers, this job-level guard prevents the common
/// case (the scheduled job racing the manual <c>POST /email-classification/run</c> trigger) cheaply,
/// without ever needing to fall back on the lock contention path. A distributed Hangfire lock
/// (backed by PostgreSQL storage, so it works correctly even if the overlapping tick lands on a
/// different server process) simply skips the second invocation instead.
/// </summary>
public class RecurringJobGuards
{
    private readonly ReminderExecutionService _reminderExecutionService;
    private readonly EscalationService _escalationService;
    private readonly EmailIntakeService _emailIntakeService;
    private readonly EmailClassificationService _emailClassificationService;

    public RecurringJobGuards(
        ReminderExecutionService reminderExecutionService,
        EscalationService escalationService,
        EmailIntakeService emailIntakeService,
        EmailClassificationService emailClassificationService)
    {
        _reminderExecutionService = reminderExecutionService;
        _escalationService = escalationService;
        _emailIntakeService = emailIntakeService;
        _emailClassificationService = emailClassificationService;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public Task RunRemindersAsync(int batchSize, CancellationToken cancellationToken) =>
        _reminderExecutionService.RunAsync(batchSize, cancellationToken);

    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public Task RunEscalationsAsync(int batchSize, CancellationToken cancellationToken) =>
        _escalationService.RunAsync(batchSize, cancellationToken);

    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public Task RunEmailIntakeAsync(CancellationToken cancellationToken) =>
        _emailIntakeService.RunAllAsync(cancellationToken);

    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public Task RunEmailClassificationAsync(int batchSize, CancellationToken cancellationToken) =>
        _emailClassificationService.RunAsync(batchSize, cancellationToken);
}
