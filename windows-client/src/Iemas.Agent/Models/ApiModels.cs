namespace Iemas.Agent.Models;

// Mirrors server/src/Iemas.Application/Agents/Dtos exactly (field-for-field, camelCase over the
// wire matching ASP.NET Core's default System.Text.Json policy) — the client is built against the
// server's actual contract, not a guessed protocol.

public record RegisterAgentRequest(string EmailAddress, string ClientName, string? ServerAddress, string? AgentVersion, string? DeviceMetadata);

public record RegisterAgentResponse(Guid AgentId, string RegistrationRequestToken, int Status);

public record AgentRegistrationStatusResponse(int Status, string? RegistrationKey, DateTimeOffset? RegistrationKeyExpiresAt, string? RejectionReason);

public record AgentAuthenticateRequest(Guid AgentId, string RegistrationKey);

public record AgentAuthenticateResponse(string AccessToken, DateTimeOffset ExpiresAt, Guid AgentId, Guid EmployeeId, string EmployeeName);

public record HeartbeatRequest(string? AgentVersion);

/// <summary>§32/§40 — mirrors server CaseDto. Enum fields arrive as strings (System.Text.Json default JsonStringEnumConverter is NOT the server's default, so these are read as ints matching the server's enum declaration order — see CaseWorkStatus/CaseReplyStatus/CaseNotificationStatus below).</summary>
public record CaseDto(
    Guid Id,
    string CaseNumber,
    Guid EmailAccountId,
    string EmailAccountAddress,
    string CustomerEmailAddress,
    string? CustomerDisplayName,
    Guid? OwnerEmployeeId,
    string? OwnerEmployeeName,
    string Subject,
    CaseWorkStatus WorkStatus,
    CaseReplyStatus ReplyStatus,
    CaseNotificationStatus NotificationStatus,
    DateTimeOffset FirstEmailReceivedAt,
    DateTimeOffset LastActivityAt,
    CaseCompletionReason? CompletionReason,
    string? CompletionComment,
    DateTimeOffset? CompletedAt,
    int ReopenCount,
    int EmailCount);

public record AgentSyncResponse(List<CaseDto> ActionRequired, List<CaseDto> Waiting, DateTimeOffset ServerTimeUtc);

/// <summary>§73 — every field §73 requires. RequestId is the idempotency key (a fresh GUID per user action, not per retry — a retried submit reuses the same RequestId so the server's idempotent replay logic applies).</summary>
public record SubmitCaseActionRequest(string RequestId, Guid CaseId, CaseActionType ActionType, string? Comment, DateTimeOffset ClientTimestamp, DateTimeOffset? RequestedForUtc = null);

public record SubmitCaseCommentRequest(string RequestId, Guid CaseId, string Comment, DateTimeOffset ClientTimestamp);

public record CaseActionResultDto(Guid CaseId, string WorkStatus, string ReplyStatus, bool WasIdempotentReplay);

public record CompleteCaseActionRequest(CaseCompletionReason Reason, string? Comment);

public record ApiErrorResponse(string? Message);

// Enums — declaration order must match the server's Iemas.Domain enums exactly, since the server
// serializes them as their underlying int by default (no JsonStringEnumConverter registered there).

public enum CaseWorkStatus { New = 0, ActionRequired = 1, InProgress = 2, WaitingForCustomer = 3, WaitingForInternal = 4, WaitingForApproval = 5, Overdue = 6, Escalated = 7, Completed = 8, Cancelled = 9, Expired = 10, ReviewRequired = 11 }

public enum CaseReplyStatus { NotApplicable = 0, AwaitingReply = 1, ReplyVerificationPending = 2, ReplyVerificationFailed = 3, ReplyNotFound = 4, Replied = 5 }

public enum CaseNotificationStatus { None = 0, Scheduled = 1, Queued = 2, Sent = 3, Delivered = 4, Displayed = 5, Acknowledged = 6, Cancelled = 7, Failed = 8, Expired = 9 }

public enum CaseCompletionReason { CustomerRequestResolved = 0, EmployeeResponded = 1, PhoneCallHandled = 2, HandledOutsideEmail = 3, NoResponseRequired = 4, Duplicate = 5, IncorrectClassification = 6, CancelledByAdmin = 7, CancelledByEmployee = 8, Other = 9 }

/// <summary>§46 — the fixed set of Case actions an employee can take via the Agent, in the server's exact declaration order (Iemas.Domain.Agents.CaseActionType).</summary>
public enum CaseActionType { Acknowledged = 0, WillHandle = 1, AlreadyReplied = 2, WaitingForCustomer = 3, WaitingForInternal = 4, RemindLater = 5, MarkCompleted = 6, Cancel = 7, Reopen = 8, RequestEscalation = 9 }

/// <summary>§68/§71 registration status, server enum order.</summary>
public enum AgentRegistrationStatus { Pending = 0, Approved = 1, Rejected = 2, Revoked = 3 }

/// <summary>
/// §72 — the closed server-to-agent push command shape (see server IAgentNotificationDispatcher /
/// AgentNotificationDispatcher). Delivered over the "AgentCommand" SignalR client method as a plain
/// JSON object with a string "type" discriminator (SHOW_NOTIFICATION/SHOW_REMINDER/SHOW_CASE/
/// CANCEL_NOTIFICATION/SYNC_REQUIRED/PING), not an int, so it is deserialized manually rather than
/// as a typed record — see SignalRAgentConnection.
/// </summary>
public record AgentPushMessage(string Type, Guid? CaseId, string? CaseNumber, string? Title, string? Message, DateTimeOffset? ServerTimeUtc);
