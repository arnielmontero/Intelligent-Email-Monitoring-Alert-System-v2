namespace Iemas.Domain.Agents;

/// <summary>Requirements §71 — Agent Registration Status.</summary>
public enum AgentRegistrationStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Revoked = 3,
}

/// <summary>Requirements §71 — Agent Connection Status.</summary>
public enum AgentConnectionStatus
{
    Connecting = 0,
    Connected = 1,
    Disconnected = 2,
}

/// <summary>Requirements §72 — the only server-to-agent commands that may ever be sent. No arbitrary remote execution.</summary>
public enum AgentCommandType
{
    ShowNotification = 0,
    ShowReminder = 1,
    ShowCase = 2,
    CancelNotification = 3,
    Sync = 4,
    Ping = 5,
}

/// <summary>Requirements §73 — the only agent-to-server action types.</summary>
public enum AgentActionType
{
    AckNotification = 0,
    CaseAction = 1,
    CaseComment = 2,
    Sync = 3,
    Pong = 4,
    Heartbeat = 5,
}

/// <summary>
/// Requirements §46 — the fixed set of Case actions an employee can take via the Agent. Stored on
/// an <see cref="AgentActionType.CaseAction"/> request. These are events/requests, not
/// automatically authoritative facts (§46) — critically, <see cref="AlreadyReplied"/> only records
/// a claim (§43); it never sets Case.ReplyStatus directly. Only Phase 6's ReplyVerificationService,
/// inspecting the real Sent mailbox, may set VerifiedReply/NoReplyFound/etc.
/// </summary>
public enum CaseActionType
{
    Acknowledged = 0,
    WillHandle = 1,
    AlreadyReplied = 2,
    WaitingForCustomer = 3,
    WaitingForInternal = 4,
    RemindLater = 5,
    MarkCompleted = 6,
    Cancel = 7,
    Reopen = 8,
    RequestEscalation = 9,
}

/// <summary>Requirements §67 "Technical Agent Log" — connect/disconnect/heartbeat/auth-failure/sync/version/error/reconnect, kept separate from Case History and the admin-facing AuditLog.</summary>
public enum AgentLogEventType
{
    RegistrationRequested = 0,
    Approved = 1,
    Rejected = 2,
    Revoked = 3,
    AuthenticationSucceeded = 4,
    AuthenticationFailed = 5,
    Connected = 6,
    Disconnected = 7,
    Heartbeat = 8,
    Sync = 9,
    VersionReported = 10,
    Error = 11,
}
