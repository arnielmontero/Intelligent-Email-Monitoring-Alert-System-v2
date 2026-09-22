using Iemas.Domain.Common;

namespace Iemas.Domain.Cases;

/// <summary>
/// Requirements §66 — Case History, append-only. "Corrections are new events rather than
/// destructive edits" — this phase never updates or deletes a CaseEvent row, only adds new ones.
/// Distinct from Employee Activity and System Audit Log (§67) — this is specifically "what
/// happened to the Case."
/// </summary>
public class CaseEvent : Entity
{
    public Guid CaseId { get; set; }
    public Case Case { get; set; } = null!;

    public CaseEventType EventType { get; set; }

    /// <summary>Human-readable summary for the §89 investigation timeline (e.g. "Matched via In-Reply-To").</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>Null for system-driven events (e.g. auto-created from email); set for admin-driven ones once Phase 5+ adds employee actions.</summary>
    public Guid? ActorEmployeeId { get; set; }

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}
