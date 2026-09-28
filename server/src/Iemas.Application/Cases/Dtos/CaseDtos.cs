using Iemas.Domain.Cases;

namespace Iemas.Application.Cases.Dtos;

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

public record CaseEventDto(
    Guid Id,
    CaseEventType EventType,
    string Detail,
    Guid? ActorEmployeeId,
    DateTimeOffset OccurredAt);

/// <summary>§66/§86 — Case History & Logs global search result: a CaseEventDto plus enough Case-identifying info to be useful outside a single Case's own detail view.</summary>
public record CaseEventSearchResultDto(
    Guid Id,
    Guid CaseId,
    string CaseNumber,
    string CaseSubject,
    CaseEventType EventType,
    string Detail,
    Guid? ActorEmployeeId,
    string? ActorEmployeeName,
    DateTimeOffset OccurredAt,
    string CustomerEmailAddress,
    string? CustomerDisplayName,
    string MailboxAddress,
    string? OwnerEmployeeName);

public record CaseEventSearchFilter(
    Guid? CaseId = null,
    CaseEventType? EventType = null,
    string? Search = null,
    Guid? EmailAccountId = null,
    Guid? OwnerEmployeeId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null);

public record CaseEmailDto(
    Guid EmailMessageId,
    string Subject,
    string FromAddress,
    DateTimeOffset ReceivedAt,
    CaseMatchSignal MatchSignal,
    string? MatchDetail,
    string? FromDisplayName = null,
    string? ToAddresses = null,
    string? CcAddresses = null,
    string? Body = null,
    bool BodyFromHtml = false,
    int AttachmentCount = 0,
    CaseEmailClassificationDto? Classification = null);

/// <summary>What the AI (and the deterministic filter) concluded about one email — §25/§89 "why was this a Case?".</summary>
public record CaseEmailClassificationDto(
    string Decision,
    string? Category,
    string? Priority,
    double? Confidence,
    string? Summary,
    bool? ActionRequired,
    string? AiModel,
    bool? Legitimate = null,
    bool? ResponseExpected = null,
    string? DecisionReason = null);

public record CaseNotificationDto(
    Guid Id,
    DateTimeOffset CreatedAt,
    string Type,
    string Status,
    string Title,
    string Message,
    string EmployeeName,
    int DeliveredAgentCount,
    DateTimeOffset? AcknowledgedAt);

public record CaseDetailDto(
    CaseDto Case,
    List<CaseEmailDto> Emails,
    List<CaseEventDto> History,
    List<ReplyVerificationAttemptDto> VerificationAttempts,
    List<CaseNotificationDto>? Notifications = null);

public record CompleteCaseRequest(CaseCompletionReason Reason, string? Comment);

public record CaseListFilter(
    CaseWorkStatus? WorkStatus,
    Guid? OwnerEmployeeId,
    Guid? EmailAccountId,
    string? CustomerEmailAddress,
    string? Search);
