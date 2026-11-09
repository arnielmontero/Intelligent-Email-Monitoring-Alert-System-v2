namespace Iemas.Domain.Cases;

/// <summary>
/// The supervision lifecycle of a customer inquiry (inquiry-supervision/PROGRESS.md, Phase 3).
/// Separate from <see cref="CaseWorkStatus"/>: WorkStatus stays as the coarse status the CMS and the
/// Windows Agent already show, and is projected from this stage; the stage is what supervision
/// timers, reminders and escalation decide on.
///
/// Overriding rule: the first agent reply never ends supervision. Only <see cref="Resolved"/> and
/// <see cref="Closed"/> are terminal.
/// </summary>
public enum InquiryStage
{
    /// <summary>New inquiry; the agent has not replied yet. Initial-response timer runs.</summary>
    AwaitingInitialResponse = 0,

    /// <summary>The agent sent the first reply. The customer is expected to answer; customer-wait timer runs.</summary>
    InitialResponseReceived = 1,

    /// <summary>The customer wrote again after the agent's reply. The agent owes the next step; idle timer runs.</summary>
    CustomerFollowUpReceived = 2,

    /// <summary>The agent sent a further reply. The customer is expected to answer; customer-wait timer runs.</summary>
    AgentFollowUpSent = 3,

    /// <summary>The agent explicitly marked the inquiry as waiting for the customer; customer-wait timer runs.</summary>
    AwaitingCustomer = 4,

    /// <summary>The agent is waiting on another department; internal-wait timer runs.</summary>
    AwaitingInternal = 5,

    /// <summary>A wait expired, or the conversation stalled. The agent owes a follow-up or a decision; idle timer runs.</summary>
    Inactive = 6,

    /// <summary>The AI believes the concern was addressed. Waiting for the agent to confirm; never closes by itself.</summary>
    PotentiallyResolved = 7,

    /// <summary>The agent confirmed resolution. Terminal: no reminders or escalation.</summary>
    Resolved = 8,

    /// <summary>Closed for another stated reason (not an inquiry, duplicate, handled elsewhere, cancelled). Terminal.</summary>
    Closed = 9,
}

/// <summary>Who wrote a message within an inquiry's conversation.</summary>
public enum ConversationRole
{
    Customer = 0,
    Agent = 1,
    /// <summary>A colleague or another department (internal domain or another monitored mailbox). Linked to the inquiry, never changes who owes the next step.</summary>
    Internal = 2,
}

/// <summary>The AI's reading of the whole conversation (Phase 5). Advisory only.</summary>
public enum ResolutionVerdict
{
    /// <summary>The agent only acknowledged ("we'll look into it"); the concern is still open.</summary>
    AcknowledgedOnly = 0,

    /// <summary>Work is under way but the concern is not yet addressed.</summary>
    InProgress = 1,

    /// <summary>The customer's concern appears to have been addressed.</summary>
    AppearsResolved = 2,

    /// <summary>The customer is waiting on the agent (an unanswered question or complaint).</summary>
    CustomerWaiting = 3,
}
