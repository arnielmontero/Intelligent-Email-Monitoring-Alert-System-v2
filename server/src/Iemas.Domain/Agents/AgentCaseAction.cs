using Iemas.Domain.Cases;
using Iemas.Domain.Common;
using Iemas.Domain.Identity;

namespace Iemas.Domain.Agents;

/// <summary>
/// Requirements §73 — every Agent-to-server CASE_ACTION request, recorded in full for idempotency
/// (§78: "Agent request IDs" are one of the explicitly required idempotency keys) and investigation.
/// Distinct from <see cref="Cases.CaseEvent"/>: this is the raw request as the Agent sent it
/// (client timestamp, request ID, which Agent/Employee sent it); CaseEvent is the resulting
/// Case-History entry. One AgentCaseAction always produces exactly one CaseEvent (when accepted).
///
/// §46 hard requirement reaffirmed here structurally: this entity has no field that lets an
/// action alone mark a Case's ReplyStatus. <see cref="CaseActionType.AlreadyReplied"/> only ever
/// results in a claim being recorded (§43) — Phase 6's ReplyVerificationService, not this table,
/// is the only writer of Case.ReplyStatus.
/// </summary>
public class AgentCaseAction : Entity
{
    /// <summary>§73/§78 — the Agent-supplied idempotency key. Unique per Agent, so a retried request never creates a duplicate action.</summary>
    public string RequestId { get; set; } = string.Empty;

    public Guid AgentId { get; set; }
    public Agent Agent { get; set; } = null!;

    /// <summary>§73 — server must validate the Agent is authorized for this Employee; always resolved from the authenticated Agent's own EmployeeId, never trusted from the request body.</summary>
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public Guid CaseId { get; set; }
    public Case Case { get; set; } = null!;

    public CaseActionType ActionType { get; set; }

    /// <summary>§47 — optional free-text comment accompanying the action (or the entire content of a standalone CASE_COMMENT).</summary>
    public string? Comment { get; set; }

    public DateTimeOffset ClientTimestamp { get; set; }
    public DateTimeOffset ServerTimestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The CaseEvent this action produced, once accepted — null only in the moment before the transaction commits.</summary>
    public Guid? CaseEventId { get; set; }
}
