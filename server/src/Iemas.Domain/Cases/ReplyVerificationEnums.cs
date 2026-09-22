namespace Iemas.Domain.Cases;

/// <summary>
/// Requirements §42 — the exact four verification states. Distinct from <see cref="CaseReplyStatus"/>
/// only in that this is the outcome of a single verification *attempt*, whereas CaseReplyStatus is
/// the Case's current standing field (which this outcome, once recorded, is used to set).
/// </summary>
public enum ReplyVerificationOutcome
{
    /// <summary>§42 VERIFIED_REPLY — a matching Sent message was found via a real identifier relationship.</summary>
    VerifiedReply = 0,

    /// <summary>§42 NO_REPLY_FOUND — Sent folder was successfully inspected; nothing matched.</summary>
    NoReplyFound = 1,

    /// <summary>§42 VERIFICATION_PENDING — not yet checked, or mailbox temporarily unavailable; will retry.</summary>
    VerificationPending = 2,

    /// <summary>§42 VERIFICATION_FAILED — the verification attempt itself errored (auth/connection/timeout).</summary>
    VerificationFailed = 3,
}

/// <summary>
/// Requirements §42/§34 — which identifier relationship matched a Sent message back to a Case,
/// mirroring <see cref="CaseMatchSignal"/>'s role for Inbox-to-Case matching. Subject is
/// deliberately absent from reply verification entirely — §42 lists it as the weakest identifier
/// but the build boundary for this phase is stricter: subject alone must never establish a
/// *verified* reply (unlike Case matching, where subject can at least create/join a Case as a
/// last resort). A Sent message that only matches by subject is not proof of a reply to a
/// specific Case's specific messages.
/// </summary>
public enum ReplyMatchSignal
{
    ThreadId = 0,
    InReplyTo = 1,
    References = 2,
    MessageIdRelationship = 3,
    RecipientAndAccountRelationship = 4,
    NoMatch = 5,
}
