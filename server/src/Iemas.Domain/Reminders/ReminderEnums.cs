namespace Iemas.Domain.Reminders;

/// <summary>
/// Requirements §54 (Reminder Engine workflow), §55 (Reminder Recheck Rule), §56 (Reminder Later).
/// A Reminder's lifecycle: Scheduled (waiting for its due time) → recheck at due time → either
/// Sent (delivered, and — unless the max-reminder ceiling was hit — a follow-up Reminder is
/// scheduled) or Cancelled (§55: the recheck found the Case no longer eligible) or Failed
/// (delivery itself failed and will be retried) or Expired (§54 "Expiration" config elapsed before
/// the reminder's due time arrived).
/// </summary>
public enum ReminderStatus
{
    Scheduled = 0,
    Sent = 1,
    Cancelled = 2,
    Failed = 3,
    Expired = 4,
}

/// <summary>
/// Requirements §55 — the specific recheck condition that caused a Scheduled reminder to be
/// cancelled instead of sent, recorded for investigation (§89) rather than left as a bare status.
/// </summary>
public enum ReminderCancelReason
{
    /// <summary>§55 "Whether reply was verified" / Case.ReplyStatus == Replied.</summary>
    ReplyVerified = 0,

    /// <summary>§55 "Whether Case was completed."</summary>
    CaseCompleted = 1,

    /// <summary>§55 "Whether Case was cancelled."</summary>
    CaseCancelled = 2,

    /// <summary>§55 "Whether employee requested another state" — e.g. WaitingForCustomer/WaitingForInternal/WaitingForApproval no longer represent unresolved "needs a reminder" work.</summary>
    CaseNoLongerActionable = 3,

    /// <summary>§55 "Whether another notification already occurred" — a newer Reminder/notification for the same Case superseded this one.</summary>
    SupersededByNewerReminder = 4,

    /// <summary>§55 "Whether escalation changed the state" — forward reference to Phase 9; honored defensively now via WorkStatus.Escalated.</summary>
    CaseEscalated = 5,

    /// <summary>§54 "Maximum reminders" ceiling reached — no further reminder is scheduled after this one, and if this Scheduled row itself is at/after the ceiling it is cancelled rather than sent.</summary>
    MaxRemindersReached = 6,

    /// <summary>An administrator or the reminder policy was disabled/removed after this reminder was scheduled.</summary>
    PolicyNoLongerApplies = 7,

    /// <summary>Manually cancelled via the CMS/API.</summary>
    ManuallyCancelled = 8,

    /// <summary>The Case itself no longer exists (defensive — should not occur given Restrict delete behavior, but re-checked explicitly per §20/§55 discipline).</summary>
    CaseNotFound = 9,
}

/// <summary>
/// Requirements §54 "No infinite reminder loops" — the source that caused a Reminder row to be
/// created, so the audit trail (§89) can explain "why was this reminder scheduled" without
/// guessing from timing alone.
/// </summary>
public enum ReminderTrigger
{
    /// <summary>§54 workflow start — "Initial Notification" → "Wait Configured Interval" — the first reminder scheduled once a Case enters ActionRequired.</summary>
    InitialActionRequired = 0,

    /// <summary>§54 "Repeat" — a follow-up reminder scheduled after a prior one was sent and the recheck still found no reply.</summary>
    FollowUp = 1,

    /// <summary>§56 "Remind me at 3:00 PM" — an employee-requested one-off reminder via the Windows Agent RemindLater action.</summary>
    EmployeeRequested = 2,
}
