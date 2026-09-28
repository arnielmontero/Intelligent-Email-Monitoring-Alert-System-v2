using System.Diagnostics;
using Hangfire;
using Iemas.Application.Cases;
using Iemas.Application.EmailClassification;
using Iemas.Application.EmailIntake;
using Iemas.Application.Escalations;
using Iemas.Application.Reminders;
using Microsoft.Extensions.Logging;
using Serilog.Context;

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
/// Intake (every minute) and AI Classification (every 2 minutes, plus straight after intake) are the tightest, so they
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
///
/// The email pipeline is chained so a customer email reaches its owner as soon as possible instead of
/// waiting for three separate timers: when intake stores new email it queues classification at once,
/// and when classification finds important email it queues the Case step at once (which sends the
/// pop-up). The queued runs go through these same guarded methods, so they can never overlap a
/// scheduled run of the same step; the recurring schedules remain as a safety net.
/// </summary>
public class RecurringJobGuards
{
    private readonly ReminderExecutionService _reminderExecutionService;
    private readonly EscalationService _escalationService;
    private readonly EmailIntakeService _emailIntakeService;
    private readonly EmailClassificationService _emailClassificationService;
    private readonly CaseWorkflowService _caseWorkflowService;
    private readonly IBackgroundJobClient _jobs;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RecurringJobGuards> _logger;

    public RecurringJobGuards(
        ReminderExecutionService reminderExecutionService,
        EscalationService escalationService,
        EmailIntakeService emailIntakeService,
        EmailClassificationService emailClassificationService,
        CaseWorkflowService caseWorkflowService,
        IBackgroundJobClient jobs,
        IConfiguration configuration,
        ILogger<RecurringJobGuards> logger)
    {
        _reminderExecutionService = reminderExecutionService;
        _escalationService = escalationService;
        _emailIntakeService = emailIntakeService;
        _emailClassificationService = emailClassificationService;
        _caseWorkflowService = caseWorkflowService;
        _jobs = jobs;
        _configuration = configuration;
        _logger = logger;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public Task RunRemindersAsync(int batchSize, CancellationToken cancellationToken) =>
        RunJobAsync("reminders", () => _reminderExecutionService.RunAsync(batchSize, cancellationToken));

    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public Task RunEscalationsAsync(int batchSize, CancellationToken cancellationToken) =>
        RunJobAsync("escalations", () => _escalationService.RunAsync(batchSize, cancellationToken));

    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public Task RunEmailIntakeAsync(CancellationToken cancellationToken) =>
        RunJobAsync("email-intake", async () =>
        {
            var results = await _emailIntakeService.RunAllAsync(cancellationToken);
            var stored = results.Sum(r => r.PersistedCount);
            if (stored > 0)
            {
                _logger.LogInformation("{Stored} new email(s) stored; starting AI classification now", stored);
                var batchSize = _configuration.GetValue("AiClassification:BatchSize", 25);
                _jobs.Enqueue<RecurringJobGuards>(guards => guards.RunEmailClassificationAsync(batchSize, CancellationToken.None));
            }
        });

    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public Task RunEmailClassificationAsync(int batchSize, CancellationToken cancellationToken) =>
        RunJobAsync("email-classification", async () =>
        {
            var result = await _emailClassificationService.RunAsync(batchSize, cancellationToken);
            if (result.ImportantCount > 0)
            {
                _logger.LogInformation("{Important} important email(s) classified; creating Cases now", result.ImportantCount);
                var caseBatchSize = _configuration.GetValue("CaseWorkflow:BatchSize", 25);
                _jobs.Enqueue<RecurringJobGuards>(guards => guards.RunCaseWorkflowAsync(caseBatchSize, CancellationToken.None));
            }
        });

    /// <summary>Case creation/matching; also sends the owner's New Email pop-up. Guarded because it now runs both on its schedule and straight after classification.</summary>
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public Task RunCaseWorkflowAsync(int batchSize, CancellationToken cancellationToken) =>
        RunJobAsync("case-workflow", () => _caseWorkflowService.RunAsync(batchSize, cancellationToken));

    // Hangfire jobs have no HTTP request to inherit a correlation ID from (CorrelationIdMiddleware
    // only covers the API pipeline), so each job execution gets its own, distinguishable by job
    // name and a short random suffix — this shows up in every structured log line for that run
    // (Serilog's LogContext is already request-scoped for HTTP, and this gives jobs the same
    // per-execution grouping) so a single run's log lines can be told apart from the next tick's.
    // Also logs start/duration/result for every job run here (not inside each service), so all 4
    // recurring jobs get consistent operational telemetry from one place, including the (previously
    // silent) common case where a run finds nothing to process.
    private async Task RunJobAsync(string jobName, Func<Task> action)
    {
        var correlationId = $"job-{jobName}-{Guid.NewGuid():n}";
        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            var stopwatch = Stopwatch.StartNew();
            _logger.LogInformation("Recurring job {JobName} starting", jobName);
            try
            {
                await action();
                _logger.LogInformation(
                    "Recurring job {JobName} completed in {ElapsedMilliseconds}ms",
                    jobName, stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Recurring job {JobName} failed after {ElapsedMilliseconds}ms",
                    jobName, stopwatch.ElapsedMilliseconds);
                throw;
            }
        }
    }
}
