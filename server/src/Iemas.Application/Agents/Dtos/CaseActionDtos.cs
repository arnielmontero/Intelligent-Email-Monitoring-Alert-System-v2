using Iemas.Domain.Agents;

namespace Iemas.Application.Agents.Dtos;

/// <summary>
/// §73 — every field §73 requires an Agent-to-server action request to contain. RequestId is the
/// §78 idempotency key; ClientTimestamp is the Agent's own clock, ServerTimestamp is assigned by
/// the server on receipt (never trusted from the client for ordering/audit purposes).
///
/// §56 "Remind me at 3:00 PM" — RequestedForUtc is only meaningful (and only read) when ActionType
/// is CaseActionType.RemindLater; every other action ignores it. Null for RemindLater means "remind
/// me after the policy's normal interval," not "remind me immediately."
/// </summary>
public record SubmitCaseActionRequest(
    string RequestId,
    Guid CaseId,
    CaseActionType ActionType,
    string? Comment,
    DateTimeOffset ClientTimestamp,
    DateTimeOffset? RequestedForUtc = null);

public record SubmitCaseCommentRequest(string RequestId, Guid CaseId, string Comment, DateTimeOffset ClientTimestamp);

public record CaseActionResultDto(Guid CaseId, string WorkStatus, string ReplyStatus, bool WasIdempotentReplay);

/// <summary>§48 — the Agent-facing MARK_COMPLETED request; a reason is mandatory, matching CasesController's admin completion endpoint.</summary>
public record CompleteCaseActionRequest(Iemas.Domain.Cases.CaseCompletionReason Reason, string? Comment);
