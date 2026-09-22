namespace Iemas.Application.Cases.Dtos;

public record ReplyVerificationRunResult(
    int ConsideredCount,
    int VerifiedCount,
    int NoReplyFoundCount,
    int PendingCount,
    int FailedCount,
    long DurationMs);

public record ReplyVerificationAttemptDto(
    Guid Id,
    Iemas.Domain.Cases.ReplyVerificationOutcome Outcome,
    string? MatchedSentMessageId,
    Iemas.Domain.Cases.ReplyMatchSignal MatchSignal,
    string? MatchDetail,
    string? ErrorDetail,
    DateTimeOffset AttemptedAt,
    long DurationMs);
