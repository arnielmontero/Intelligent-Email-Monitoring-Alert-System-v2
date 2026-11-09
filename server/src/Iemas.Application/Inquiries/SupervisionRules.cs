using Iemas.Domain.Cases;
using Iemas.Domain.Notifications;
using Iemas.Domain.Reminders;

namespace Iemas.Application.Inquiries;

public enum SupervisionActionKind
{
    /// <summary>Record the resolution target (first evaluation of a cycle). No notification.</summary>
    SetResolutionDue,

    /// <summary>A response/idle deadline passed. Recorded on the timeline; the first reminder follows one interval later.</summary>
    DeadlineMissed,

    /// <summary>Remind the agent (counts toward escalation).</summary>
    Reminder,

    /// <summary>A wait expired: tell the agent and move the inquiry to Inactive (the agent now owes the next step).</summary>
    WaitExpired,

    /// <summary>The customer stayed silent while the inquiry looked resolved: ask the agent to review and confirm.</summary>
    ConfirmationFollowUp,

    /// <summary>The resolution target passed: mark overdue and tell the agent.</summary>
    MarkOverdue,

    /// <summary>Notify management (next escalation level).</summary>
    Escalate,
}

public sealed record SupervisionAction(
    SupervisionActionKind Kind,
    string Reason,
    ReminderTrigger? Trigger = null,
    NotificationType? Notification = null,
    DateTimeOffset? Value = null,
    bool IsOverdueEscalation = false);

/// <summary>
/// The timer rules of the supervision engine (Q1 decisions). Pure and clock-explicit: given an
/// inquiry, the settings, the business calendar and "now", it returns the single next action that
/// is due, or null. The engine applies one action per inquiry per run, so a backlog after downtime
/// is worked off step by step rather than as a burst.
///
/// Separate timers, by stage:
///   Agent owes a reply  - initial response (4 business h) / customer follow-up (1 business day) /
///                         inactive (1 business day): deadline, then reminders every 2 business h,
///                         then escalation one interval after the last reminder, then the next
///                         escalation level every N business days while still neglected.
///   Customer owes reply - 3 calendar days, then the agent is told a follow-up may be needed and
///                         the inquiry becomes Inactive (agent owes the next step). Never escalated
///                         merely because the customer is silent.
///   Internal wait       - 1 business day without activity, then the agent is asked for an update.
///   Potentially resolved- never escalated, never closed: after 3 calendar days of customer silence
///                         the agent is asked to review and confirm, then gently reminded.
///   Resolution target   - 5 business days per cycle: marks overdue; escalates only while the agent
///                         owes the next step.
/// </summary>
public static class SupervisionRules
{
    public static SupervisionAction? Evaluate(Case c, SupervisionSettings s, BusinessCalendar cal, DateTimeOffset now)
    {
        if (InquiryLifecycle.IsTerminal(c.Stage)) return null;

        var cycleStart = c.SupervisionCycleStartedAt ?? c.FirstEmailReceivedAt;
        if (c.ResolutionDueAt is null)
        {
            return new SupervisionAction(SupervisionActionKind.SetResolutionDue,
                $"Resolution target: {s.ResolutionTargetBusinessDays} business day(s).",
                Value: cal.AddBusinessDays(cycleStart, s.ResolutionTargetBusinessDays));
        }

        if (c.OverdueSince is null && now >= c.ResolutionDueAt)
        {
            return new SupervisionAction(SupervisionActionKind.MarkOverdue,
                $"Not resolved within the {s.ResolutionTargetBusinessDays}-business-day target.",
                ReminderTrigger.ResolutionOverdue, NotificationType.Overdue, c.ResolutionDueAt);
        }

        var stageAction = c.Stage switch
        {
            InquiryStage.AwaitingInitialResponse => AgentLadder(c, s, cal, now,
                cal.AddBusinessHours(c.StageChangedAt, s.FirstResponseBusinessHours),
                $"No initial response within {s.FirstResponseBusinessHours} business hour(s)",
                ReminderTrigger.ResponseOverdue, escalate: true, firstReminderType: NotificationType.FirstReminder, laterType: NotificationType.Reminder),

            InquiryStage.CustomerFollowUpReceived => AgentLadder(c, s, cal, now,
                cal.AddBusinessDays(c.StageChangedAt, s.IdleBusinessDays),
                $"The customer's follow-up has had no response for {s.IdleBusinessDays} business day(s)",
                ReminderTrigger.ResponseOverdue, escalate: true, firstReminderType: NotificationType.Reminder, laterType: NotificationType.Reminder),

            InquiryStage.Inactive => AgentLadder(c, s, cal, now,
                cal.AddBusinessDays(c.StageChangedAt, s.IdleBusinessDays),
                $"The conversation has made no progress for {s.IdleBusinessDays} business day(s)",
                ReminderTrigger.ConversationIdle, escalate: true, firstReminderType: NotificationType.UpdateRequested, laterType: NotificationType.UpdateRequested),

            InquiryStage.InitialResponseReceived or InquiryStage.AgentFollowUpSent or InquiryStage.AwaitingCustomer =>
                now >= c.StageChangedAt.AddDays(s.CustomerWaitCalendarDays)
                    ? new SupervisionAction(SupervisionActionKind.WaitExpired,
                        $"The customer has not replied for {s.CustomerWaitCalendarDays} calendar day(s); a follow-up may be needed.",
                        ReminderTrigger.CustomerSilent, NotificationType.CustomerFollowUpDue)
                    : null,

            InquiryStage.AwaitingInternal =>
                now >= cal.AddBusinessDays(Latest(c.StageChangedAt, c.LastInternalActivityAt, c.LastAgentMessageAt), s.InternalWaitBusinessDays)
                    ? new SupervisionAction(SupervisionActionKind.WaitExpired,
                        $"No internal progress for {s.InternalWaitBusinessDays} business day(s); the agent is asked for an update.",
                        ReminderTrigger.InternalWaitExpired, NotificationType.UpdateRequested)
                    : null,

            InquiryStage.PotentiallyResolved => PotentiallyResolvedRules(c, s, cal, now),

            _ => null,
        };
        if (stageAction is not null) return stageAction;

        // Overdue escalation: only while the agent owes the next step, one interval after the
        // overdue notice, and not if this episode already escalated (management already knows).
        if (c.OverdueSince is DateTimeOffset overdueSince
            && c.OverdueEscalatedAt is null
            && c.EpisodeEscalations == 0
            && InquiryLifecycle.AgentOwesNextStep(c.Stage)
            && now >= cal.AddBusinessHours(Latest(overdueSince, c.StageChangedAt), s.ReminderIntervalBusinessHours))
        {
            return new SupervisionAction(SupervisionActionKind.Escalate,
                $"Unresolved beyond the {s.ResolutionTargetBusinessDays}-business-day resolution target while the agent owes the next step.",
                IsOverdueEscalation: true);
        }

        return null;
    }

    private static SupervisionAction? AgentLadder(
        Case c, SupervisionSettings s, BusinessCalendar cal, DateTimeOffset now, DateTimeOffset deadline, string what,
        ReminderTrigger trigger, bool escalate, NotificationType firstReminderType, NotificationType laterType)
    {
        if (now < deadline) return null;

        if (c.EpisodeDeadlineMissedAt is null)
        {
            return new SupervisionAction(SupervisionActionKind.DeadlineMissed, $"{what} (deadline {deadline:u}).", trigger, Value: deadline);
        }

        var interval = s.ReminderIntervalBusinessHours;
        var sent = c.EpisodeRemindersSent;
        if (sent < s.RemindersBeforeEscalation)
        {
            var due = cal.AddBusinessHours(deadline, interval * (sent + 1));
            if (now < due) return null;
            var isWarning = escalate && sent + 1 == s.RemindersBeforeEscalation;
            return new SupervisionAction(SupervisionActionKind.Reminder,
                $"{what}. Reminder {sent + 1} of {s.RemindersBeforeEscalation}.",
                trigger, isWarning ? NotificationType.EscalationWarning : sent == 0 ? firstReminderType : laterType);
        }

        if (!escalate) return null;

        if (c.EpisodeEscalations == 0)
        {
            var due = cal.AddBusinessHours(deadline, interval * (s.RemindersBeforeEscalation + 1));
            return now < due ? null : new SupervisionAction(SupervisionActionKind.Escalate,
                $"{what}, and still unanswered after {s.RemindersBeforeEscalation} reminder(s).");
        }

        var repeatDue = cal.AddBusinessDays(c.EpisodeLastEscalatedAt ?? deadline, s.EscalationRepeatBusinessDays);
        return now < repeatDue ? null : new SupervisionAction(SupervisionActionKind.Escalate,
            $"{what}, still neglected {s.EscalationRepeatBusinessDays} business day(s) after the last escalation.");
    }

    /// <summary>Q2: never closes, never escalates. Customer silence for the wait period → ask the agent to review and confirm; then gentle reminders.</summary>
    private static SupervisionAction? PotentiallyResolvedRules(Case c, SupervisionSettings s, BusinessCalendar cal, DateTimeOffset now)
    {
        if (c.EpisodeWaitNoticeAt is null)
        {
            return now >= c.StageChangedAt.AddDays(s.CustomerWaitCalendarDays)
                ? new SupervisionAction(SupervisionActionKind.ConfirmationFollowUp,
                    $"The customer has not replied for {s.CustomerWaitCalendarDays} calendar day(s) while the inquiry looks resolved. "
                    + "A follow-up may be appropriate; please review and confirm whether it can be closed.",
                    ReminderTrigger.ConfirmResolution, NotificationType.CustomerFollowUpDue)
                : null;
        }

        return AgentLadder(c, s, cal, now, cal.AddBusinessDays(c.EpisodeWaitNoticeAt.Value, s.IdleBusinessDays),
            "The inquiry still awaits the agent's resolution confirmation",
            ReminderTrigger.ConfirmResolution, escalate: false, NotificationType.ConfirmResolution, NotificationType.ConfirmResolution);
    }

    private static DateTimeOffset Latest(DateTimeOffset first, params DateTimeOffset?[] others)
    {
        var latest = first;
        foreach (var o in others)
        {
            if (o is DateTimeOffset value && value > latest) latest = value;
        }
        return latest;
    }
}
