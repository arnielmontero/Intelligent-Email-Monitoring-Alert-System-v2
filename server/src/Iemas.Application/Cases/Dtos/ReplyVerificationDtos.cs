namespace Iemas.Application.Cases.Dtos;

public record ReplyVerificationRunResult(
    int ConsideredCount,
    int VerifiedCount,
    int NoReplyFoundCount,
    int PendingCount,
    int FailedCount,
    long DurationMs,
    List<ReplyCheckCaseDto>? Items = null);

public record ReplyCheckCaseDto(
    Guid CaseId,
    string CaseNumber,
    string Subject,
    string Customer,
    string Mailbox,
    string? Owner,
    string ReplyStatus,
    DateTimeOffset? LastCheckedAt,
    string? LastResult,
    DateTimeOffset LastActivityAt);

public record MailboxReplyCheckDto(Guid EmailAccountId, string Mailbox, int OpenCases, DateTimeOffset? LastCheckedAt, string? LastProblem);

public record ReplyCheckOverviewDto(
    int AwaitingFirstCheck,
    int NoReplyYet,
    int ReplyFound,
    int CouldNotCheck,
    List<MailboxReplyCheckDto> Mailboxes);

public record ReplyVerificationAttemptDto(
    Guid Id,
    Iemas.Domain.Cases.ReplyVerificationOutcome Outcome,
    string? MatchedSentMessageId,
    Iemas.Domain.Cases.ReplyMatchSignal MatchSignal,
    string? MatchDetail,
    string? ErrorDetail,
    DateTimeOffset AttemptedAt,
    long DurationMs);
