using Iemas.Domain.Common;

namespace Iemas.Domain.Cases;

/// <summary>
/// Requirements §42 (Reply Verification Engine) and §89 (Case Investigation — "why was the reply
/// considered verified?"). One row per verification attempt, append-only (mirrors
/// <see cref="CaseEvent"/>'s discipline) — a re-check does not overwrite the previous attempt's
/// record, so the full verification history for a Case is reconstructable. The Case's current
/// standing (<see cref="Case.ReplyStatus"/>) is a projection of the latest attempt's outcome, but
/// this table is the auditable source of every attempt, not just the most recent one.
/// </summary>
public class ReplyVerificationAttempt : Entity
{
    public Guid CaseId { get; set; }
    public Case Case { get; set; } = null!;

    public ReplyVerificationOutcome Outcome { get; set; }

    /// <summary>
    /// Set only when Outcome is VerifiedReply. Sent messages are read live from the mailbox each
    /// verification pass — they are not persisted as EmailMessage rows (Sent items are outside
    /// the Phase 3 intake pipeline) — so the matched message is identified by its own RFC 5322
    /// Message-ID / provider identifier here, not a foreign key into email_messages.
    /// </summary>
    public string? MatchedSentMessageId { get; set; }
    public ReplyMatchSignal MatchSignal { get; set; } = ReplyMatchSignal.NoMatch;
    public string? MatchDetail { get; set; }

    /// <summary>Set when Outcome is VerificationFailed — the provider/auth/timeout error, never a secret (§16).</summary>
    public string? ErrorDetail { get; set; }

    public DateTimeOffset AttemptedAt { get; set; } = DateTimeOffset.UtcNow;
    public long DurationMs { get; set; }
}
