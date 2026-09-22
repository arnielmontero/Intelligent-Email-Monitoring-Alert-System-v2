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

public record CaseEmailDto(
    Guid EmailMessageId,
    string Subject,
    string FromAddress,
    DateTimeOffset ReceivedAt,
    CaseMatchSignal MatchSignal,
    string? MatchDetail);

public record CaseDetailDto(CaseDto Case, List<CaseEmailDto> Emails, List<CaseEventDto> History, List<ReplyVerificationAttemptDto> VerificationAttempts);

public record CompleteCaseRequest(CaseCompletionReason Reason, string? Comment);

public record CaseListFilter(
    CaseWorkStatus? WorkStatus,
    Guid? OwnerEmployeeId,
    Guid? EmailAccountId,
    string? CustomerEmailAddress,
    string? Search);
