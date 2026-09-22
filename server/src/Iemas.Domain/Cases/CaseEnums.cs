namespace Iemas.Domain.Cases;

/// <summary>Requirements §40 — Work Status.</summary>
public enum CaseWorkStatus
{
    New = 0,
    ActionRequired = 1,
    InProgress = 2,
    WaitingForCustomer = 3,
    WaitingForInternal = 4,
    WaitingForApproval = 5,
    Overdue = 6,
    Escalated = 7,
    Completed = 8,
    Cancelled = 9,
    Expired = 10,
    ReviewRequired = 11,
}

/// <summary>
/// Requirements §40 (Reply Status) and §42 (Reply Verification Engine verification states — the
/// four values named there are used verbatim: VerifiedReply, NoReplyFound, VerificationPending,
/// VerificationFailed). NotApplicable/AwaitingReply are the pre-verification states from §40 that
/// exist before Phase 6's verification engine has anything to check yet.
/// </summary>
public enum CaseReplyStatus
{
    NotApplicable = 0,
    AwaitingReply = 1,

    /// <summary>§42 VERIFICATION_PENDING — a verification attempt is scheduled/in progress; also used when the mailbox could not be inspected (§44), never NoReplyFound in that case.</summary>
    VerificationPending = 2,

    /// <summary>§42 VERIFICATION_FAILED — the verification attempt itself failed (provider/auth/timeout error), distinct from a clean "checked and found nothing."</summary>
    VerificationFailed = 3,

    /// <summary>§42 NO_REPLY_FOUND — the Sent mailbox was successfully inspected and no matching reply was found.</summary>
    NoReplyFound = 4,

    /// <summary>§42 VERIFIED_REPLY — a matching Sent message was found via a real identifier relationship, not subject alone.</summary>
    Replied = 5,
}

/// <summary>Requirements §40 — Notification Status. Values beyond None/Scheduled are Phase 8/9 territory but the enum is defined now so Case can carry the field.</summary>
public enum CaseNotificationStatus
{
    None = 0,
    Scheduled = 1,
    Queued = 2,
    Sent = 3,
    Delivered = 4,
    Displayed = 5,
    Acknowledged = 6,
    Cancelled = 7,
    Failed = 8,
    Expired = 9,
}

/// <summary>Requirements §48 — Completion reasons, required when marking a Case Completed.</summary>
public enum CaseCompletionReason
{
    CustomerRequestResolved = 0,
    EmployeeResponded = 1,
    PhoneCallHandled = 2,
    HandledOutsideEmail = 3,
    NoResponseRequired = 4,
    Duplicate = 5,
    IncorrectClassification = 6,
    CancelledByAdmin = 7,
    CancelledByEmployee = 8,
    Other = 9,
}

/// <summary>
/// Requirements §34 — the matching signal that associated a message with its Case, recorded for
/// investigation (§89 "why was this Case matched this way?"). Ordered here purely for documentation;
/// the actual priority order lives in CaseMatchingService.
/// </summary>
public enum CaseMatchSignal
{
    ThreadId = 0,
    InReplyTo = 1,
    References = 2,
    MessageIdRelationship = 3,
    ParticipantAndAccountRelationship = 4,
    RecentConversationContext = 5,
    NormalizedSubjectWeakSignal = 6,
    NewCase = 7,
}

/// <summary>Requirements §66 — Case History event types (the ones built so far; later phases add more).</summary>
public enum CaseEventType
{
    EmailReceived = 0,
    Created = 1,
    Updated = 2,
    StatusChanged = 3,
    Reopened = 4,
    Completed = 5,
    Cancelled = 6,

    /// <summary>§42/§66 — a reply verification attempt occurred and produced an outcome, regardless of which outcome.</summary>
    ReplyVerification = 7,

    /// <summary>§46 — an employee took a predefined Case action via the Windows Agent. §46: "Employee actions are events/requests. They are not automatically authoritative facts."</summary>
    EmployeeAction = 8,

    /// <summary>§47 — a free-text employee comment/update, distinct from a status-changing action.</summary>
    EmployeeComment = 9,

    /// <summary>§54/§89 — a Reminder was scheduled, sent, cancelled, or expired. Detail distinguishes which.</summary>
    ReminderEvent = 10,

    /// <summary>§63 — an escalation attempt occurred (executed, skipped, or failed to resolve a recipient). Detail distinguishes which; the full record lives in EscalationEvent.</summary>
    EscalationEvent = 11,
}
