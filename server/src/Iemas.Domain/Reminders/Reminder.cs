using Iemas.Domain.Cases;
using Iemas.Domain.Common;

namespace Iemas.Domain.Reminders;

/// <summary>
/// Requirements §54-§56 — one scheduled/executed reminder instance for a Case. Append-only history
/// (§89): a reminder is never deleted or overwritten in place, only transitioned Scheduled → one of
/// Sent/Cancelled/Failed/Expired, so the full sequence of what was scheduled and why remains
/// auditable even after the Case is resolved.
///
/// §41 Critical Status Rule reaffirmed here: sending a reminder is not the same event as the
/// employee acknowledging it, replying, or the Case completing — this row only ever represents the
/// reminder-engine side of that chain (§53 "Notification vs Action Required": a Reminder is a
/// Notification, not itself Action Required work).
/// </summary>
public class Reminder : Entity
{
    public Guid CaseId { get; set; }
    public Case Case { get; set; } = null!;

    public Guid? ReminderPolicyId { get; set; }
    public ReminderPolicy? ReminderPolicy { get; set; }

    public ReminderTrigger Trigger { get; set; }
    public ReminderStatus Status { get; set; } = ReminderStatus.Scheduled;

    /// <summary>1-based — the Nth reminder for this Case's current (post-last-reopen) reminder cycle. Compared against ReminderPolicy.MaxReminders (§54 "No infinite reminder loops").</summary>
    public int SequenceNumber { get; set; }

    /// <summary>When this reminder is/was due to fire, already adjusted for business hours/weekends/holidays at scheduling time. The Hangfire job re-validates this is still correct at execution time (§55) rather than trusting it blindly.</summary>
    public DateTimeOffset ScheduledForUtc { get; set; }

    /// <summary>§56 — set only for ReminderTrigger.EmployeeRequested, the exact moment the employee asked to be reminded (e.g. "3:00 PM"), before business-hours adjustment. Null for automatic reminders.</summary>
    public DateTimeOffset? RequestedForUtc { get; set; }

    /// <summary>§56/§73 — the Windows Agent request that produced this reminder, when Trigger is EmployeeRequested. Enables idempotency: the same (AgentId, RequestId) RemindLater action must never schedule two reminders.</summary>
    public Guid? SourceAgentCaseActionId { get; set; }

    public DateTimeOffset? ExecutedAtUtc { get; set; }
    public DateTimeOffset? CancelledAtUtc { get; set; }
    public ReminderCancelReason? CancelReason { get; set; }
    public string? CancelDetail { get; set; }

    /// <summary>§54 "Retry" — how many delivery attempts have failed so far for this specific Scheduled reminder (not the same counter as SequenceNumber, which counts reminders, not attempts).</summary>
    public int DeliveryAttempts { get; set; }
    public string? LastFailureDetail { get; set; }

    /// <summary>Idempotency for the Hangfire execution job itself (§78): a specific Reminder row may only be "claimed" for execution once per due firing, guarded by this + a unique index, same DbUpdateException race pattern used throughout.</summary>
    public string? ExecutionClaimToken { get; set; }
}
