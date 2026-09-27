using System.Diagnostics;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Escalations;
using Iemas.Application.Notifications;
using Iemas.Application.Operations;
using Iemas.Application.Reminders.Dtos;
using Iemas.Domain.Cases;
using Iemas.Domain.Notifications;
using Iemas.Domain.Operations;
using Iemas.Domain.Reminders;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Reminders;

/// <summary>
/// Requirements §54 (workflow execution), §55 (Reminder Recheck Rule — "Before sending any
/// scheduled reminder, IEMAS must re-check..."), §56 (Remind Later execution). This is the
/// Hangfire recurring job body. §53 "Notification vs Action Required" is preserved: sending a
/// reminder here only ever changes Reminder/Case.NotificationStatus fields, never
/// Case.WorkStatus/ReplyStatus — those remain owned by CaseWorkflowService/ReplyVerificationService/
/// AgentCaseActionService respectively.
///
/// Delivery goes through <see cref="NotificationService"/>: the §51 CMS-editable template is
/// rendered, recorded as a Notification (§104), then pushed as §72 SHOW_REMINDER. "Sent" still means "the recheck
/// passed and the reminder was durably marked ready," recorded via Case.NotificationStatus and the
/// CaseEvent trail (the Reminder row's own Status is the authoritative record) — delivery is a
/// best-effort push on top of that already-committed state, never a precondition for it (§76: the
/// DB transaction always commits first; delivery failure never changes Reminder/Case state).
/// </summary>
public class ReminderExecutionService
{
    private const int MaxDeliveryAttempts = 3;

    private readonly IAppDbContext _db;
    private readonly ReminderSchedulingService _schedulingService;
    private readonly NotificationService? _notificationService;

    public ReminderExecutionService(IAppDbContext db, ReminderSchedulingService schedulingService, NotificationService? notificationService = null)
    {
        _db = db;
        _schedulingService = schedulingService;
        _notificationService = notificationService;
    }

    /// <summary>
    /// Runs one execution cycle: claims every Scheduled reminder whose ScheduledForUtc has arrived,
    /// rechecks it (§55), and sends/cancels/expires/retries accordingly. Re-derives its candidate
    /// set fresh every call (§20), safe to run concurrently/after a restart — a reminder claimed by
    /// a prior, now-dead run but never resolved is simply picked up again since only a terminal
    /// Status (Sent/Cancelled/Failed-exhausted/Expired) removes it from the candidate query.
    /// </summary>
    public async Task<ReminderRunResult> RunAsync(int batchSize, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var now = DateTimeOffset.UtcNow;

        // §91 — reminders also hold while Agent Notifications are paused, so a reminder the
        // employee could never see is not counted as sent toward an escalation threshold.
        if (await _db.IsPausedAsync(PauseControl.Reminders, cancellationToken)
            || await _db.IsPausedAsync(PauseControl.AgentNotifications, cancellationToken))
        {
            return new ReminderRunResult(0, 0, 0, 0, 0, 0, stopwatch.ElapsedMilliseconds);
        }

        var candidateIds = await _db.Reminders
            .Where(r => r.Status == ReminderStatus.Scheduled && r.ScheduledForUtc <= now)
            .OrderBy(r => r.ScheduledForUtc)
            .Take(batchSize)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);

        int sent = 0, cancelled = 0, failed = 0, expired = 0, rescheduled = 0;

        foreach (var reminderId in candidateIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await ProcessOneAsync(reminderId, cancellationToken);
            switch (outcome)
            {
                case ExecutionOutcome.Sent: sent++; if (await ScheduleFollowUpIfEligibleAsync(reminderId, cancellationToken)) rescheduled++; break;
                case ExecutionOutcome.Cancelled: cancelled++; break;
                case ExecutionOutcome.Failed: failed++; break;
                case ExecutionOutcome.Expired: expired++; break;
            }
        }

        stopwatch.Stop();
        return new ReminderRunResult(candidateIds.Count, sent, cancelled, failed, expired, rescheduled, stopwatch.ElapsedMilliseconds);
    }

    private enum ExecutionOutcome { Sent, Cancelled, Failed, Skipped, Expired }

    /// <summary>
    /// One reminder, fully rechecked and resolved. Public for direct unit testing of a single
    /// reminder's recheck/execution path without needing a full RunAsync batch.
    /// </summary>
    public async Task<bool> ProcessOneReminderForTestAsync(Guid reminderId, CancellationToken cancellationToken)
        => await ProcessOneAsync(reminderId, cancellationToken) == ExecutionOutcome.Sent;

    private async Task<ExecutionOutcome> ProcessOneAsync(Guid reminderId, CancellationToken cancellationToken)
    {
        // §78 idempotency — claim this reminder by stamping a unique token before doing anything
        // else. A concurrent/duplicate run racing on the same row loses the unique-index race and
        // is treated as "someone else is already handling this," not a failure.
        var claimToken = Guid.NewGuid().ToString("N");
        var reminder = await _db.Reminders.FirstOrDefaultAsync(r => r.Id == reminderId && r.Status == ReminderStatus.Scheduled, cancellationToken);
        if (reminder is null)
        {
            return ExecutionOutcome.Skipped;
        }
        reminder.ExecutionClaimToken = claimToken;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another concurrent run claimed it first (or it was already re-resolved) — not this run's to process.
            return ExecutionOutcome.Skipped;
        }

        var policy = reminder.ReminderPolicyId is Guid policyId
            ? await _db.ReminderPolicies.FirstOrDefaultAsync(p => p.Id == policyId, cancellationToken)
            : null;

        // §54 "Expiration" — a reminder that sat Scheduled far longer than the policy allows
        // (e.g. an extended server/Hangfire outage) is Expired rather than sent stale/late.
        if (policy?.ExpirationWindow is TimeSpan expirationWindow
            && DateTimeOffset.UtcNow - reminder.ScheduledForUtc > expirationWindow)
        {
            return await ResolveAsync(reminder, ReminderStatus.Expired, null, null, cancellationToken);
        }

        var targetCase = await _db.Cases.FirstOrDefaultAsync(c => c.Id == reminder.CaseId, cancellationToken);

        // §55 Reminder Recheck Rule — every condition it lists, checked fresh right now, not
        // trusted from whatever was true when this reminder was originally scheduled.
        var cancelReason = DetermineCancelReason(targetCase);
        if (cancelReason is ReminderCancelReason reason)
        {
            return await ResolveAsync(reminder, ReminderStatus.Cancelled, reason, DescribeCancelReason(reason), cancellationToken);
        }

        // Passed every recheck — hand off for delivery. TryDeliverAsync's own doc explains the
        // durability/best-effort split: DB state (below, in ResolveAsync) is authoritative regardless
        // of whether the live push actually reached a connected Agent.
        var deliverySucceeded = await TryDeliverAsync(reminder, targetCase!, cancellationToken);
        if (!deliverySucceeded)
        {
            reminder.DeliveryAttempts++;
            reminder.LastFailureDetail = "Delivery attempt failed.";
            if (reminder.DeliveryAttempts >= MaxDeliveryAttempts)
            {
                return await ResolveAsync(reminder, ReminderStatus.Failed, null, $"Delivery failed after {reminder.DeliveryAttempts} attempts.", cancellationToken);
            }

            // §54 "Retry" — release the claim and leave it Scheduled so the next run retries it,
            // rather than treating a single delivery failure as terminal.
            reminder.ExecutionClaimToken = null;
            await _db.SaveChangesAsync(cancellationToken);
            return ExecutionOutcome.Failed;
        }

        return await ResolveAsync(reminder, ReminderStatus.Sent, null, null, cancellationToken);
    }

    /// <summary>
    /// §72 SHOW_REMINDER push via the §51 notification templates — best-effort only. "Delivery
    /// succeeded" means the durable Sent decision is final, not that a human saw a toast: an
    /// offline Agent catches up on its next SYNC (§74/§75), so this never blocks the Reminder's
    /// own terminal state.
    /// </summary>
    private async Task<bool> TryDeliverAsync(Reminder reminder, Case targetCase, CancellationToken cancellationToken)
    {
        if (_notificationService is null)
        {
            return true;
        }

        var sentSoFar = await _db.Reminders.CountAsync(r => r.CaseId == targetCase.Id && r.Status == ReminderStatus.Sent, cancellationToken);
        var thisReminderCount = sentSoFar + 1;
        var type = await ChooseNotificationTypeAsync(reminder, targetCase, sentSoFar, thisReminderCount, cancellationToken);

        await _notificationService.SendForCaseAsync(type, targetCase, AgentPushCommandType.ShowReminder, thisReminderCount, cancellationToken);
        return true;
    }

    /// <summary>§52 — the reminder that reaches the escalation policy's trigger count carries the Escalation Warning instead of a plain reminder.</summary>
    private async Task<NotificationType> ChooseNotificationTypeAsync(
        Reminder reminder, Case targetCase, int sentSoFar, int thisReminderCount, CancellationToken cancellationToken)
    {
        if (reminder.Trigger != ReminderTrigger.EmployeeRequested)
        {
            var escalationPolicy = await EscalationService.ResolvePolicyAsync(_db, targetCase, cancellationToken);
            if (escalationPolicy is not null && thisReminderCount == escalationPolicy.TriggerReminderCount)
            {
                return NotificationType.EscalationWarning;
            }
        }

        return sentSoFar == 0 ? NotificationType.FirstReminder : NotificationType.Reminder;
    }

    private async Task<ExecutionOutcome> ResolveAsync(
        Reminder reminder, ReminderStatus status, ReminderCancelReason? cancelReason, string? detail, CancellationToken cancellationToken)
    {
        reminder.Status = status;
        reminder.ExecutionClaimToken = null;

        if (status == ReminderStatus.Sent)
        {
            reminder.ExecutedAtUtc = DateTimeOffset.UtcNow;
        }
        else if (status is ReminderStatus.Cancelled or ReminderStatus.Expired or ReminderStatus.Failed)
        {
            reminder.CancelledAtUtc = DateTimeOffset.UtcNow;
            reminder.CancelReason = cancelReason;
            reminder.CancelDetail = detail;
        }

        var targetCase = await _db.Cases.FirstOrDefaultAsync(c => c.Id == reminder.CaseId, cancellationToken);
        if (targetCase is not null)
        {
            targetCase.NotificationStatus = status switch
            {
                ReminderStatus.Sent => Domain.Cases.CaseNotificationStatus.Sent,
                ReminderStatus.Cancelled => Domain.Cases.CaseNotificationStatus.Cancelled,
                ReminderStatus.Expired => Domain.Cases.CaseNotificationStatus.Expired,
                ReminderStatus.Failed => Domain.Cases.CaseNotificationStatus.Failed,
                _ => targetCase.NotificationStatus,
            };
            targetCase.UpdatedAt = DateTimeOffset.UtcNow;

            _db.CaseEvents.Add(new CaseEvent
            {
                CaseId = reminder.CaseId,
                EventType = CaseEventType.ReminderEvent,
                Detail = status switch
                {
                    ReminderStatus.Sent => $"Reminder #{reminder.SequenceNumber} sent.",
                    ReminderStatus.Cancelled => $"Reminder #{reminder.SequenceNumber} cancelled: {detail}",
                    ReminderStatus.Expired => $"Reminder #{reminder.SequenceNumber} expired before it could be sent.",
                    ReminderStatus.Failed => $"Reminder #{reminder.SequenceNumber} failed: {detail}",
                    _ => $"Reminder #{reminder.SequenceNumber} updated.",
                },
            });
        }

        await _db.SaveChangesAsync(cancellationToken);

        return status switch
        {
            ReminderStatus.Sent => ExecutionOutcome.Sent,
            ReminderStatus.Cancelled => ExecutionOutcome.Cancelled,
            ReminderStatus.Expired => ExecutionOutcome.Expired,
            ReminderStatus.Failed => ExecutionOutcome.Failed,
            _ => ExecutionOutcome.Skipped,
        };
    }

    /// <summary>§55 — every recheck condition it lists, in the order given. Returns null when the Case is still legitimately eligible.</summary>
    private static ReminderCancelReason? DetermineCancelReason(Case? targetCase)
    {
        if (targetCase is null) return ReminderCancelReason.CaseNotFound;
        if (targetCase.ReplyStatus == CaseReplyStatus.Replied) return ReminderCancelReason.ReplyVerified;
        if (targetCase.WorkStatus == CaseWorkStatus.Completed) return ReminderCancelReason.CaseCompleted;
        if (targetCase.WorkStatus == CaseWorkStatus.Cancelled) return ReminderCancelReason.CaseCancelled;
        if (targetCase.WorkStatus == CaseWorkStatus.Escalated) return ReminderCancelReason.CaseEscalated;
        if (targetCase.WorkStatus is CaseWorkStatus.WaitingForCustomer or CaseWorkStatus.WaitingForInternal or CaseWorkStatus.WaitingForApproval)
        {
            return ReminderCancelReason.CaseNoLongerActionable;
        }
        return null;
    }

    private static string DescribeCancelReason(ReminderCancelReason reason) => reason switch
    {
        ReminderCancelReason.ReplyVerified => "reply was verified since this reminder was scheduled.",
        ReminderCancelReason.CaseCompleted => "Case was completed since this reminder was scheduled.",
        ReminderCancelReason.CaseCancelled => "Case was cancelled since this reminder was scheduled.",
        ReminderCancelReason.CaseEscalated => "Case was escalated since this reminder was scheduled.",
        ReminderCancelReason.CaseNoLongerActionable => "Case moved to a waiting state that no longer requires a reminder.",
        ReminderCancelReason.CaseNotFound => "Case could not be found.",
        _ => reason.ToString(),
    };

    /// <summary>§54 "Repeat" step — after a successful send, schedule the next one unless the policy's ceiling was reached (handled inside ScheduleFollowUpReminderAsync itself).</summary>
    private async Task<bool> ScheduleFollowUpIfEligibleAsync(Guid sentReminderId, CancellationToken cancellationToken)
    {
        var sentReminder = await _db.Reminders.AsNoTracking().FirstOrDefaultAsync(r => r.Id == sentReminderId, cancellationToken);
        if (sentReminder is null) return false;

        var followUp = await _schedulingService.ScheduleFollowUpReminderAsync(sentReminder, cancellationToken);
        return followUp is not null;
    }

    /// <summary>§55/CMS — manual cancellation, e.g. an administrator cancelling a reminder directly.</summary>
    public async Task<bool> CancelAsync(Guid reminderId, string? detail, CancellationToken cancellationToken)
    {
        var reminder = await _db.Reminders.FirstOrDefaultAsync(r => r.Id == reminderId && r.Status == ReminderStatus.Scheduled, cancellationToken);
        if (reminder is null) return false;

        await ResolveAsync(reminder, ReminderStatus.Cancelled, ReminderCancelReason.ManuallyCancelled, detail ?? "Manually cancelled.", cancellationToken);
        return true;
    }

    /// <summary>§54 rescheduling — cancels the existing Scheduled reminder and creates a new one at the requested time via the normal scheduling path (so business-hours/min-interval rules still apply), rather than mutating ScheduledForUtc in place, preserving the append-only history.</summary>
    public async Task<bool> RescheduleAsync(Guid reminderId, DateTimeOffset newScheduledForUtc, CancellationToken cancellationToken)
    {
        var reminder = await _db.Reminders.FirstOrDefaultAsync(r => r.Id == reminderId && r.Status == ReminderStatus.Scheduled, cancellationToken);
        if (reminder is null) return false;

        var policy = reminder.ReminderPolicyId is Guid policyId
            ? await _db.ReminderPolicies.Include(p => p.Holidays).FirstOrDefaultAsync(p => p.Id == policyId, cancellationToken)
            : null;

        await ResolveAsync(reminder, ReminderStatus.Cancelled, ReminderCancelReason.SupersededByNewerReminder, "Rescheduled by administrator.", cancellationToken);

        var adjustedUtc = policy is not null
            ? ReminderTimingCalculator.AdjustToAllowedWindow(policy, newScheduledForUtc, policy.Holidays.Select(h => h.Date).ToList())
            : newScheduledForUtc;

        var targetCase = await _db.Cases.FirstOrDefaultAsync(c => c.Id == reminder.CaseId, cancellationToken);
        if (targetCase is null) return false;

        var replacement = new Reminder
        {
            CaseId = reminder.CaseId,
            ReminderPolicyId = reminder.ReminderPolicyId,
            Trigger = reminder.Trigger,
            Status = ReminderStatus.Scheduled,
            SequenceNumber = reminder.SequenceNumber,
            ScheduledForUtc = adjustedUtc,
            RequestedForUtc = reminder.RequestedForUtc,
        };
        _db.Reminders.Add(replacement);

        _db.CaseEvents.Add(new CaseEvent
        {
            CaseId = reminder.CaseId,
            EventType = CaseEventType.ReminderEvent,
            Detail = $"Reminder #{reminder.SequenceNumber} rescheduled to {adjustedUtc:u}.",
        });

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
