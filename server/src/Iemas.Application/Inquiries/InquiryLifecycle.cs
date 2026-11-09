using Iemas.Domain.Cases;

namespace Iemas.Application.Inquiries;

/// <summary>A stage change, recorded on the Case timeline.</summary>
public sealed record StageTransition(InquiryStage From, InquiryStage To, DateTimeOffset At, string Reason)
{
    public string Describe() => $"Stage: {InquiryLifecycle.Label(From)} → {InquiryLifecycle.Label(To)}. {Reason}";
}

/// <summary>
/// The single place that decides inquiry stage transitions (Phase 3). Pure: it only mutates the
/// Case passed in and returns what changed; callers persist and record history.
///
/// Rules that hold everywhere:
/// 1. The first agent reply never ends supervision. Only <see cref="InquiryStage.Resolved"/> (agent
///    confirmed) and <see cref="InquiryStage.Closed"/> (closed for a stated reason) are terminal.
/// 2. Message-driven changes are timestamped with the message's own time, and a message older than
///    the current stage (processed late) never rewinds it - it is still recorded as activity.
/// 3. Every stage change starts a fresh "episode": reminder and escalation counters for the previous
///    stage no longer apply.
/// 4. The AI can only propose <see cref="InquiryStage.PotentiallyResolved"/>.
/// </summary>
public static class InquiryLifecycle
{
    public static bool IsTerminal(InquiryStage stage) => stage is InquiryStage.Resolved or InquiryStage.Closed;

    /// <summary>The agent owes the next step: these stages are neglect if left alone.</summary>
    public static bool AgentOwesNextStep(InquiryStage stage) =>
        stage is InquiryStage.AwaitingInitialResponse or InquiryStage.CustomerFollowUpReceived or InquiryStage.Inactive;

    /// <summary>The customer owes the next step: silence here is not the agent's fault.</summary>
    public static bool CustomerOwesNextStep(InquiryStage stage) =>
        stage is InquiryStage.InitialResponseReceived or InquiryStage.AgentFollowUpSent or InquiryStage.AwaitingCustomer;

    public static string Label(InquiryStage stage) => stage switch
    {
        InquiryStage.AwaitingInitialResponse => "Awaiting initial response",
        InquiryStage.InitialResponseReceived => "Initial response sent",
        InquiryStage.CustomerFollowUpReceived => "Customer follow-up received",
        InquiryStage.AgentFollowUpSent => "Agent follow-up sent",
        InquiryStage.AwaitingCustomer => "Awaiting customer",
        InquiryStage.AwaitingInternal => "Awaiting internal action",
        InquiryStage.Inactive => "Conversation inactive",
        InquiryStage.PotentiallyResolved => "Potentially resolved",
        InquiryStage.Resolved => "Resolved",
        InquiryStage.Closed => "Closed",
        _ => stage.ToString(),
    };

    /// <summary>A new inquiry: the agent owes the first reply.</summary>
    public static StageTransition Start(Case c, DateTimeOffset firstEmailAt)
    {
        c.LastCustomerMessageAt = Max(c.LastCustomerMessageAt, firstEmailAt);
        c.SupervisionCycleStartedAt = firstEmailAt;
        var from = c.Stage;
        c.Stage = InquiryStage.AwaitingInitialResponse;
        c.StageChangedAt = firstEmailAt;
        ResetEpisode(c);
        ProjectWorkStatus(c);
        return new StageTransition(from, c.Stage, firstEmailAt, "New customer inquiry; the agent owes an initial response.");
    }

    /// <summary>The customer (or someone on the customer's side) wrote in this conversation.</summary>
    public static StageTransition? OnCustomerMessage(Case c, DateTimeOffset at)
    {
        c.LastCustomerMessageAt = Max(c.LastCustomerMessageAt, at);
        if (at < c.StageChangedAt) return null;

        if (IsTerminal(c.Stage))
        {
            // A customer writing again after closure reopens the inquiry: a new supervision cycle.
            var reopened = MoveTo(c, InquiryStage.CustomerFollowUpReceived, at,
                $"The customer wrote again after the inquiry was {Label(c.Stage).ToLowerInvariant()}; reopened.");
            c.ReopenCount++;
            c.CompletionReason = null;
            c.CompletionComment = null;
            c.CompletedAt = null;
            c.SupervisionCycleStartedAt = at;
            c.ResolutionDueAt = null;
            c.OverdueSince = null;
            c.OverdueEscalatedAt = null;
            ProjectWorkStatus(c);
            return reopened;
        }

        // A customer nudging before the first reply must not extend the agent's deadline.
        if (c.Stage == InquiryStage.AwaitingInitialResponse) return null;

        // Already waiting on the agent: the clock keeps running from the first unanswered message.
        if (c.Stage == InquiryStage.CustomerFollowUpReceived) return null;

        return MoveTo(c, InquiryStage.CustomerFollowUpReceived, at, c.Stage switch
        {
            InquiryStage.PotentiallyResolved => "The customer wrote again while the inquiry looked resolved; the agent owes a response.",
            InquiryStage.AwaitingInternal => "The customer wrote while the agent was waiting on another department; the agent owes a response.",
            _ => "The customer sent a follow-up; the agent owes a response.",
        });
    }

    /// <summary>The agent sent a message to the customer (found in the Sent folder).</summary>
    public static StageTransition? OnAgentMessage(Case c, DateTimeOffset at)
    {
        c.LastAgentMessageAt = Max(c.LastAgentMessageAt, at);
        var first = c.FirstAgentResponseAt is null;
        if (first) c.FirstAgentResponseAt = at;

        if (at < c.StageChangedAt || IsTerminal(c.Stage)) return null;

        // Waiting on another department is an explicit agent decision; a customer update does not end it.
        if (c.Stage == InquiryStage.AwaitingInternal) return null;

        // The Sent folder is read less often than the Inbox, so the customer's answer to this reply may
        // already be on record. Then the agent owes the next step again, from the customer's message.
        if (c.LastCustomerMessageAt is DateTimeOffset customerSince && customerSince > at)
        {
            return MoveTo(c, InquiryStage.CustomerFollowUpReceived, customerSince,
                $"The agent replied ({(first ? "initial response" : "follow-up")}), and the customer has written again since; the agent owes a response.");
        }

        if (c.Stage == InquiryStage.AwaitingInitialResponse || first)
        {
            return MoveTo(c, InquiryStage.InitialResponseReceived, at,
                "The agent sent the initial response. Supervision continues until the inquiry is resolved.");
        }

        return MoveTo(c, InquiryStage.AgentFollowUpSent, at, c.Stage == InquiryStage.PotentiallyResolved
            ? "The agent wrote again while the inquiry looked resolved; the conversation continues."
            : "The agent sent a follow-up; waiting for the customer.");
    }

    /// <summary>Internal activity (a forward to another department, a colleague's reply). Never changes who owes the next step.</summary>
    public static void OnInternalActivity(Case c, DateTimeOffset at) =>
        c.LastInternalActivityAt = Max(c.LastInternalActivityAt, at);

    public static StageTransition? OnAgentMarkedWaitingForCustomer(Case c, DateTimeOffset at) =>
        IsTerminal(c.Stage) ? null : MoveTo(c, InquiryStage.AwaitingCustomer, at, "The agent marked the inquiry as waiting for the customer.");

    public static StageTransition? OnAgentMarkedWaitingForInternal(Case c, DateTimeOffset at) =>
        IsTerminal(c.Stage) ? null : MoveTo(c, InquiryStage.AwaitingInternal, at, "The agent marked the inquiry as waiting for internal action.");

    /// <summary>A wait limit expired or the conversation stalled: the agent owes a follow-up or a decision.</summary>
    public static StageTransition? OnWaitExpired(Case c, DateTimeOffset at, string reason) =>
        IsTerminal(c.Stage) ? null : MoveTo(c, InquiryStage.Inactive, at, reason);

    /// <summary>The AI believes the concern was addressed. Advisory only: the agent must confirm.</summary>
    public static StageTransition? OnPotentiallyResolved(Case c, DateTimeOffset at, string reason)
    {
        if (IsTerminal(c.Stage) || c.Stage == InquiryStage.PotentiallyResolved) return null;

        // Before any agent reply there is nothing to have resolved the concern.
        if (c.FirstAgentResponseAt is null) return null;

        var previous = c.Stage;
        var transition = MoveTo(c, InquiryStage.PotentiallyResolved, at, reason);
        c.StageBeforePotentiallyResolved = previous;
        return transition;
    }

    /// <summary>The agent rejected the AI's suggestion: back to where the conversation was, supervision continues.</summary>
    public static StageTransition? OnResolutionRejected(Case c, DateTimeOffset at)
    {
        if (c.Stage != InquiryStage.PotentiallyResolved) return null;
        var back = c.StageBeforePotentiallyResolved is { } s && !IsTerminal(s) && s != InquiryStage.PotentiallyResolved
            ? s
            : InquiryStage.Inactive;
        c.StageBeforePotentiallyResolved = null;
        return MoveTo(c, back, at, "The agent said the inquiry is not resolved yet; supervision continues.");
    }

    /// <summary>The agent confirmed resolution (final authority). Ends supervision.</summary>
    public static StageTransition? OnResolutionConfirmed(Case c, DateTimeOffset at, string reason) =>
        c.Stage == InquiryStage.Resolved ? null : MoveTo(c, InquiryStage.Resolved, at, reason);

    /// <summary>Closed for another stated reason (not an inquiry, duplicate, cancelled...). Ends supervision.</summary>
    public static StageTransition? OnClosed(Case c, DateTimeOffset at, string reason) =>
        c.Stage == InquiryStage.Closed ? null : MoveTo(c, InquiryStage.Closed, at, reason);

    /// <summary>An agent or administrator reopened a closed inquiry by hand: the agent owes the next step.</summary>
    public static StageTransition? OnReopenedByAgent(Case c, DateTimeOffset at)
    {
        if (!IsTerminal(c.Stage)) return null;
        c.SupervisionCycleStartedAt = at;
        c.ResolutionDueAt = null;
        c.OverdueSince = null;
        c.OverdueEscalatedAt = null;
        return MoveTo(c, InquiryStage.Inactive, at, "Reopened by hand; the agent owes the next step.");
    }

    /// <summary>
    /// Keeps the existing coarse <see cref="CaseWorkStatus"/> (shown in the CMS and Windows Agent)
    /// consistent with the stage. Terminal WorkStatus values (Completed/Cancelled) are set by the
    /// caller that knows the completion reason and are left alone here.
    /// </summary>
    public static void ProjectWorkStatus(Case c)
    {
        c.WorkStatus = c.Stage switch
        {
            InquiryStage.AwaitingInitialResponse or InquiryStage.CustomerFollowUpReceived or InquiryStage.Inactive =>
                c.EpisodeEscalations > 0 ? CaseWorkStatus.Escalated
                : c.OverdueSince is not null ? CaseWorkStatus.Overdue
                : CaseWorkStatus.ActionRequired,
            InquiryStage.InitialResponseReceived or InquiryStage.AgentFollowUpSent => CaseWorkStatus.InProgress,
            InquiryStage.AwaitingCustomer => CaseWorkStatus.WaitingForCustomer,
            InquiryStage.AwaitingInternal => CaseWorkStatus.WaitingForInternal,
            InquiryStage.PotentiallyResolved => CaseWorkStatus.PotentiallyResolved,
            InquiryStage.Resolved => CaseWorkStatus.Completed,
            InquiryStage.Closed => c.WorkStatus is CaseWorkStatus.Completed or CaseWorkStatus.Cancelled ? c.WorkStatus : CaseWorkStatus.Cancelled,
            _ => c.WorkStatus,
        };
    }

    private static StageTransition MoveTo(Case c, InquiryStage to, DateTimeOffset at, string reason)
    {
        var from = c.Stage;
        c.Stage = to;
        c.StageChangedAt = at;
        if (to != InquiryStage.PotentiallyResolved) c.StageBeforePotentiallyResolved = null;
        ResetEpisode(c);
        ProjectWorkStatus(c);
        c.UpdatedAt = DateTimeOffset.UtcNow;
        return new StageTransition(from, to, at, reason);
    }

    private static void ResetEpisode(Case c)
    {
        c.EpisodeDeadlineMissedAt = null;
        c.EpisodeWaitNoticeAt = null;
        c.EpisodeRemindersSent = 0;
        c.EpisodeLastReminderAt = null;
        c.EpisodeEscalations = 0;
        c.EpisodeLastEscalatedAt = null;
    }

    private static DateTimeOffset Max(DateTimeOffset? current, DateTimeOffset candidate) =>
        current is DateTimeOffset value && value > candidate ? value : candidate;
}
