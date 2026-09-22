using Iemas.Application.Common;
using Iemas.Application.Common.Interfaces;
using Iemas.Domain.Cases;
using Iemas.Domain.Reminders;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Reminders;

/// <summary>
/// Requirements §54 (workflow: Initial Notification → Wait Configured Interval → ... → Repeat),
/// §56 (Remind Later). Owns creation of <see cref="Reminder"/> rows only — never sends anything and
/// never mutates Case.WorkStatus/ReplyStatus itself; delivery + recheck + cancellation live in
/// <see cref="ReminderExecutionService"/>, matching the deterministic-pre-filter /
/// decision-execution separation already used for classification/case-matching/reply-matching.
/// </summary>
public class ReminderSchedulingService
{
    private readonly IAppDbContext _db;

    public ReminderSchedulingService(IAppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// §54 "Initial Notification → Wait Configured Interval" — call once a Case newly enters
    /// ActionRequired (from CaseWorkflowService's Created/Updated/Reopened paths) to start its
    /// reminder cycle. Idempotent: a Case with any non-terminal Reminder already scheduled is left
    /// alone rather than double-scheduled.
    /// </summary>
    public async Task<Reminder?> ScheduleInitialReminderAsync(Guid caseId, CancellationToken cancellationToken)
    {
        var targetCase = await _db.Cases.FirstOrDefaultAsync(c => c.Id == caseId, cancellationToken);
        if (targetCase is null || !IsReminderEligible(targetCase))
        {
            return null;
        }

        var alreadyScheduled = await _db.Reminders
            .AnyAsync(r => r.CaseId == caseId && r.Status == ReminderStatus.Scheduled, cancellationToken);
        if (alreadyScheduled)
        {
            return null;
        }

        var policy = await ResolvePolicyAsync(targetCase, cancellationToken);
        if (policy is null)
        {
            return null;
        }

        var dueUtc = await AdjustAsync(policy, DateTimeOffset.UtcNow.Add(policy.InitialDelay), cancellationToken);

        return await CreateReminderAsync(targetCase, policy, ReminderTrigger.InitialActionRequired, sequenceNumber: 1, dueUtc, requestedForUtc: null, sourceAgentCaseActionId: null, cancellationToken);
    }

    /// <summary>
    /// §54 "Repeat" — called by the execution job right after a reminder is Sent, provided the
    /// policy's MaxReminders ceiling has not been reached.
    /// </summary>
    public async Task<Reminder?> ScheduleFollowUpReminderAsync(Reminder sentReminder, CancellationToken cancellationToken)
    {
        var targetCase = await _db.Cases.FirstOrDefaultAsync(c => c.Id == sentReminder.CaseId, cancellationToken);
        if (targetCase is null || !IsReminderEligible(targetCase))
        {
            return null;
        }

        var policy = sentReminder.ReminderPolicyId is Guid policyId
            ? await _db.ReminderPolicies.Include(p => p.Holidays).FirstOrDefaultAsync(p => p.Id == policyId, cancellationToken)
            : await ResolvePolicyAsync(targetCase, cancellationToken);
        if (policy is null || !policy.Enabled)
        {
            return null;
        }

        var nextSequence = sentReminder.SequenceNumber + 1;
        if (nextSequence > policy.MaxReminders)
        {
            // §54 "No infinite reminder loops" — the ceiling is enforced here structurally: this
            // method simply never creates a row beyond MaxReminders. Nothing elsewhere needs its
            // own separate cap check to stay safe.
            return null;
        }

        var dueUtc = await AdjustAsync(policy, DateTimeOffset.UtcNow.Add(policy.ReminderInterval), cancellationToken);

        return await CreateReminderAsync(targetCase, policy, ReminderTrigger.FollowUp, nextSequence, dueUtc, requestedForUtc: null, sourceAgentCaseActionId: null, cancellationToken);
    }

    /// <summary>
    /// §56 "Remind me at 3:00 PM" — one-off, employee-requested reminder triggered by the
    /// RemindLater Windows Agent action. Idempotent per AgentCaseAction via the unique index on
    /// Reminder.SourceAgentCaseActionId (a retried RemindLater request must never schedule twice).
    /// </summary>
    public async Task<Result<Reminder>> ScheduleEmployeeRequestedReminderAsync(
        Guid caseId, Guid sourceAgentCaseActionId, DateTimeOffset? requestedForUtc, CancellationToken cancellationToken)
    {
        var existing = await _db.Reminders.FirstOrDefaultAsync(r => r.SourceAgentCaseActionId == sourceAgentCaseActionId, cancellationToken);
        if (existing is not null)
        {
            return Result<Reminder>.Success(existing);
        }

        var targetCase = await _db.Cases.FirstOrDefaultAsync(c => c.Id == caseId, cancellationToken);
        if (targetCase is null)
        {
            return Result<Reminder>.Failure("Case not found.");
        }
        if (!IsReminderEligible(targetCase))
        {
            return Result<Reminder>.Failure("Case is no longer eligible for reminders (already resolved).");
        }

        var policy = await ResolvePolicyAsync(targetCase, cancellationToken);
        if (policy is null)
        {
            return Result<Reminder>.Failure("No enabled Reminder Policy applies to this Case.");
        }

        // §54 "Minimum interval" applies to Remind Later too — an employee cannot request a
        // reminder sooner than the policy's floor after the most recent one for this Case.
        var requestedRaw = requestedForUtc ?? DateTimeOffset.UtcNow.Add(policy.ReminderInterval);
        var earliestAllowed = await EarliestAllowedByMinimumIntervalAsync(targetCase.Id, policy, cancellationToken);
        var candidate = requestedRaw > earliestAllowed ? requestedRaw : earliestAllowed;

        var dueUtc = await AdjustAsync(policy, candidate, cancellationToken);
        var sequence = await NextSequenceNumberAsync(targetCase.Id, cancellationToken);

        var reminder = await CreateReminderAsync(targetCase, policy, ReminderTrigger.EmployeeRequested, sequence, dueUtc, requestedForUtc, sourceAgentCaseActionId, cancellationToken);
        if (reminder is null)
        {
            return Result<Reminder>.Failure("Unable to schedule reminder for this Case's current state.");
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            var raced = await _db.Reminders.AsNoTracking().FirstOrDefaultAsync(r => r.SourceAgentCaseActionId == sourceAgentCaseActionId, cancellationToken);
            if (raced is null) throw;
            return Result<Reminder>.Success(raced);
        }

        return Result<Reminder>.Success(reminder);
    }

    /// <summary>§40/§45/§48/§49 — a Case is only worth reminding about while it genuinely still needs employee attention and has not been verifiably replied to.</summary>
    private static bool IsReminderEligible(Case c) =>
        c.WorkStatus is CaseWorkStatus.New or CaseWorkStatus.ActionRequired or CaseWorkStatus.InProgress or CaseWorkStatus.Overdue
        && c.ReplyStatus != CaseReplyStatus.Replied;

    private async Task<DateTimeOffset> EarliestAllowedByMinimumIntervalAsync(Guid caseId, ReminderPolicy policy, CancellationToken cancellationToken)
    {
        var lastForCase = await _db.Reminders
            .Where(r => r.CaseId == caseId)
            .OrderByDescending(r => r.ScheduledForUtc)
            .Select(r => (DateTimeOffset?)r.ScheduledForUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (lastForCase is null) return DateTimeOffset.UtcNow;
        var floor = lastForCase.Value.Add(policy.MinimumInterval);
        return floor > DateTimeOffset.UtcNow ? floor : DateTimeOffset.UtcNow;
    }

    private async Task<int> NextSequenceNumberAsync(Guid caseId, CancellationToken cancellationToken)
    {
        var max = await _db.Reminders.Where(r => r.CaseId == caseId).Select(r => (int?)r.SequenceNumber).MaxAsync(cancellationToken);
        return (max ?? 0) + 1;
    }

    private async Task<DateTimeOffset> AdjustAsync(ReminderPolicy policy, DateTimeOffset candidateUtc, CancellationToken cancellationToken)
    {
        var holidays = policy.Holidays.Count > 0
            ? policy.Holidays.Select(h => h.Date).ToList()
            : await _db.ReminderPolicyHolidays.Where(h => h.ReminderPolicyId == policy.Id).Select(h => h.Date).ToListAsync(cancellationToken);

        return ReminderTimingCalculator.AdjustToAllowedWindow(policy, candidateUtc, holidays);
    }

    /// <summary>§54 policy resolution — the most specific enabled policy wins: by ClassificationProfile first, IsDefault fallback otherwise. No policy at all (none configured, or the applicable one disabled) means no reminder is scheduled; this is a deliberate no-op, not an error.</summary>
    private async Task<ReminderPolicy?> ResolvePolicyAsync(Case targetCase, CancellationToken cancellationToken)
    {
        var caseEmailMessageIds = _db.CaseEmails.Where(ce => ce.CaseId == targetCase.Id).Select(ce => ce.EmailMessageId);
        var classification = await _db.EmailClassifications.AsNoTracking()
            .Where(c => caseEmailMessageIds.Contains(c.EmailMessageId))
            .Select(c => (Guid?)c.ClassificationProfileId)
            .FirstOrDefaultAsync(cancellationToken);

        if (classification is Guid profileId)
        {
            var scoped = await _db.ReminderPolicies.Include(p => p.Holidays)
                .FirstOrDefaultAsync(p => p.Enabled && p.ClassificationProfileId == profileId, cancellationToken);
            if (scoped is not null) return scoped;
        }

        return await _db.ReminderPolicies.Include(p => p.Holidays)
            .FirstOrDefaultAsync(p => p.Enabled && p.IsDefault, cancellationToken);
    }

    private async Task<Reminder?> CreateReminderAsync(
        Case targetCase, ReminderPolicy policy, ReminderTrigger trigger, int sequenceNumber, DateTimeOffset dueUtc,
        DateTimeOffset? requestedForUtc, Guid? sourceAgentCaseActionId, CancellationToken cancellationToken)
    {
        var reminder = new Reminder
        {
            CaseId = targetCase.Id,
            ReminderPolicyId = policy.Id,
            Trigger = trigger,
            Status = ReminderStatus.Scheduled,
            SequenceNumber = sequenceNumber,
            ScheduledForUtc = dueUtc,
            RequestedForUtc = requestedForUtc,
            SourceAgentCaseActionId = sourceAgentCaseActionId,
        };
        _db.Reminders.Add(reminder);

        _db.CaseEvents.Add(new CaseEvent
        {
            CaseId = targetCase.Id,
            EventType = CaseEventType.ReminderEvent,
            Detail = trigger == ReminderTrigger.EmployeeRequested
                ? $"Reminder #{sequenceNumber} scheduled for {dueUtc:u} (employee requested)."
                : $"Reminder #{sequenceNumber} scheduled for {dueUtc:u} ({trigger}).",
        });

        if (sourceAgentCaseActionId is null)
        {
            // Follow-up/initial reminders have no external idempotency key to race on; save
            // immediately so callers observe a persisted row (mirrors CaseWorkflowService).
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                var stillEligible = await _db.Reminders.AnyAsync(r => r.CaseId == targetCase.Id && r.Status == ReminderStatus.Scheduled, cancellationToken);
                if (!stillEligible) throw;
                return null;
            }
        }

        return reminder;
    }
}
