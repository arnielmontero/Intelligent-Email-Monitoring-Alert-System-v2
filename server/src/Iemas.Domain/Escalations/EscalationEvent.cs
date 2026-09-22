using Iemas.Domain.Cases;
using Iemas.Domain.Common;

namespace Iemas.Domain.Escalations;

/// <summary>
/// Requirements §63 — the escalation audit record: "Every escalation becomes a Case History child
/// event," storing Case ID, Policy ID, Level, Trigger, Recipient, Channel, Timestamp, Email Message
/// ID, Result, Delivery result, Retry count, Failure reason. Append-only (§66 discipline, same as
/// Reminder/CaseEvent) — a re-attempted level creates a new row, never overwrites this one.
///
/// §64 "Escalation Does Not Automatically Transfer Ownership" is enforced by construction: this
/// entity has no field that could be mistaken for Case ownership, and no code anywhere writes to
/// Case.OwnerEmployeeId as a side effect of escalation.
/// </summary>
public class EscalationEvent : Entity
{
    public Guid CaseId { get; set; }
    public Case Case { get; set; } = null!;

    public Guid? EscalationPolicyId { get; set; }
    public EscalationPolicy? EscalationPolicy { get; set; }

    /// <summary>§58 — 1-based level this event represents.</summary>
    public int Level { get; set; }

    /// <summary>§63 "Trigger" — human-readable description of what made this level eligible (e.g. "Reminder #3 sent, grace period elapsed").</summary>
    public string Trigger { get; set; } = string.Empty;

    public EscalationRecipientType? RecipientType { get; set; }

    /// <summary>§63 "Recipient" — the resolved recipient's display identity (name/email), not a live FK — the recipient at the time of escalation must remain in the audit trail even if the employee is later deleted/reassigned.</summary>
    public string? RecipientDisplay { get; set; }
    public Guid? RecipientEmployeeId { get; set; }

    /// <summary>§63/§57 "Channel."</summary>
    public string? Channel { get; set; }

    public EscalationOutcome Outcome { get; set; }
    public EscalationSkipReason? SkipReason { get; set; }
    public string? Detail { get; set; }

    /// <summary>§63 "Email Message ID" / delivery-layer fields — carried here as nullable pass-through columns for a later phase's delivery layer to populate; this phase never writes them.</summary>
    public string? EmailMessageId { get; set; }
    public string? DeliveryResult { get; set; }
    public int RetryCount { get; set; }
    public string? FailureReason { get; set; }

    /// <summary>Idempotency: at most one Executed/RecipientUnresolved/Failed event per (CaseId, EscalationPolicyId, Level) — a Skipped event (e.g. ThresholdNotReached, re-recorded every poll) is deliberately excluded from this guard; see EscalationConfiguration.</summary>
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}
