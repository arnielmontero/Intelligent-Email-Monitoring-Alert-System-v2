using Hangfire;
using Iemas.Application.EmailIntake;
using Iemas.Application.Escalations;
using Iemas.Application.Reminders;

namespace Iemas.Api.Jobs;

/// <summary>
/// Phase 10 hardening: thin wrappers so <see cref="DisableConcurrentExecutionAttribute"/> can sit on
/// the exact method Hangfire invokes for the Reminder, Escalation, and Email Intake recurring jobs.
/// The attribute only works via Hangfire's own job filter pipeline, which requires a Hangfire
/// package reference — something <c>Iemas.Application</c> deliberately does not take (Application
/// defines interfaces only; Infrastructure/Api are the only layers allowed a framework dependency),
/// so the guard lives here instead of directly on <see cref="ReminderExecutionService.RunAsync"/>/
/// <see cref="EscalationService.RunAsync"/>/<see cref="EmailIntakeService.RunAllAsync"/>.
///
/// Each service's own row-level idempotency (Reminder's <c>ExecutionClaimToken</c> unique-index race,
/// Escalation's per-level Executed-row check, Intake's UID-watermark) already prevents a *duplicate
/// send/escalation/message* even under concurrent execution — this attribute is defense-in-depth
/// against a slower failure mode: an overrunning run (e.g. a slow mailbox or DB) still being
/// mid-batch when its own next cron tick fires, which would otherwise mean two instances racing
/// over the same candidate set, most of that work thrown away, and doubled DB/IMAP load during
/// exactly the conditions (a struggling dependency) where that load is least wanted. Email Intake
/// is the tightest interval of any job here (every 2 minutes, vs. 5/10 for Reminder/Escalation), so
/// it is the job most likely to genuinely overlap its own next tick under a slow/degraded mailbox —
/// exactly the case Phase 10's IMAP retry/backoff work makes take longer per attempt on a struggling
/// server, raising the overlap risk this guard closes. A distributed Hangfire lock (backed by
/// PostgreSQL storage, so it works correctly even if the overlapping tick lands on a different
/// server process) simply skips the second invocation instead.
/// </summary>
public class RecurringJobGuards
{
    private readonly ReminderExecutionService _reminderExecutionService;
    private readonly EscalationService _escalationService;
    private readonly EmailIntakeService _emailIntakeService;

    public RecurringJobGuards(
        ReminderExecutionService reminderExecutionService,
        EscalationService escalationService,
        EmailIntakeService emailIntakeService)
    {
        _reminderExecutionService = reminderExecutionService;
        _escalationService = escalationService;
        _emailIntakeService = emailIntakeService;
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
}
