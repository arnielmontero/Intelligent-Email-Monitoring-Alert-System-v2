using Iemas.Application.Common;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Notifications;
using Iemas.Application.Operations;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Iemas.Domain.Notifications;
using Iemas.Domain.Reminders;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Inquiries;

/// <summary>
/// Connects the existing pipeline (Case creation, Sent-folder sync, agent actions, CMS completion)
/// to <see cref="InquiryLifecycle"/>. Every stage change goes through here so it is recorded on the
/// Case timeline the same way, whichever component caused it.
///
/// Existing services take this as an optional collaborator. Production always provides it; whether
/// the new supervision behaviour is active is the <c>inquiry.supervision_enabled</c> setting.
/// </summary>
public class InquiryConversationService
{
    private readonly IAppDbContext _db;
    private readonly NotificationService? _notifications;

    public InquiryConversationService(IAppDbContext db, NotificationService? notifications = null)
    {
        _db = db;
        _notifications = notifications;
    }

    public Task<bool> IsSupervisionEnabledAsync(CancellationToken cancellationToken) =>
        SystemSettingsService.GetBoolAsync(_db, SystemSettingKeys.SupervisionEnabled, cancellationToken);

    /// <summary>Q3 - who wrote an inbound message: the customer's side, or someone internal (internal domain, one of our own mailboxes).</summary>
    public async Task<ConversationRole> InboundRoleAsync(string fromAddress, CancellationToken cancellationToken)
    {
        if (await IsInternalAsync(fromAddress, cancellationToken)) return ConversationRole.Internal;
        return ConversationRole.Customer;
    }

    /// <summary>
    /// An outbound message counts as the agent replying to the customer when it went to the customer
    /// or to anyone outside the company. Sent only to internal addresses, it is an internal forward:
    /// linked to the inquiry, but not a reply. The agent's own domain being internal never excludes it (Q3).
    /// </summary>
    public async Task<ConversationRole> OutboundRoleAsync(string customerAddress, IEnumerable<string> recipients, CancellationToken cancellationToken)
    {
        foreach (var recipient in recipients)
        {
            if (string.Equals(recipient, customerAddress, StringComparison.OrdinalIgnoreCase)) return ConversationRole.Agent;
            if (!await IsInternalAsync(recipient, cancellationToken)) return ConversationRole.Agent;
        }
        return ConversationRole.Internal;
    }

    public async Task<bool> IsInternalAsync(string? address, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        var internalSenders = IgnoredSenderList.Parse(await SystemSettingsService.GetValueAsync(_db, SystemSettingKeys.InternalDomains, cancellationToken));
        if (internalSenders.Matches(address)) return true;
        var lower = address.Trim().ToLower();
        return await _db.EmailAccounts.AnyAsync(a => a.EmailAddress.ToLower() == lower, cancellationToken);
    }

    /// <summary>A Case was just created from a customer email.</summary>
    public void OnCaseCreated(Case c, EmailMessage message) =>
        Record(c, InquiryLifecycle.Start(c, message.ReceivedAt));

    /// <summary>An inbound message was attached to an existing inquiry. Returns true if the inquiry was reopened.</summary>
    public async Task<bool> OnInboundAttachedAsync(Case c, EmailMessage message, ConversationRole role, CancellationToken cancellationToken)
    {
        if (role == ConversationRole.Internal)
        {
            InquiryLifecycle.OnInternalActivity(c, message.ReceivedAt);
            AddEvent(c, CaseEventType.InternalActivity, $"Internal email from {message.FromAddress} linked to this inquiry: \"{message.Subject}\". Who owes the next step is unchanged.", message.ReceivedAt);
            return false;
        }

        var wasTerminal = InquiryLifecycle.IsTerminal(c.Stage);
        var transition = InquiryLifecycle.OnCustomerMessage(c, message.ReceivedAt);
        Record(c, transition);

        if (transition is not null && InquiryLifecycle.AgentOwesNextStep(c.Stage))
        {
            c.ReplyStatus = CaseReplyStatus.AwaitingReply;
            await CancelPendingSupervisionRemindersAsync(c.Id, cancellationToken);
        }
        return wasTerminal && transition is not null;
    }

    /// <summary>An outbound message from the agent's Sent folder was linked to the inquiry.</summary>
    public void OnOutboundAttached(Case c, EmailMessage message, ConversationRole role)
    {
        if (role == ConversationRole.Internal)
        {
            InquiryLifecycle.OnInternalActivity(c, message.ReceivedAt);
            AddEvent(c, CaseEventType.InternalActivity,
                $"The agent forwarded or wrote internally ({message.ToAddresses}): \"{message.Subject}\". The inquiry stays under monitoring.", message.ReceivedAt);
            return;
        }

        var first = c.FirstAgentResponseAt is null;
        var transition = InquiryLifecycle.OnAgentMessage(c, message.ReceivedAt);
        AddEvent(c, CaseEventType.AgentMessage,
            $"{(first ? "Initial response" : "Agent reply")} sent to {message.ToAddresses}: \"{message.Subject}\".", message.ReceivedAt);
        Record(c, transition);
        c.ReplyStatus = CaseReplyStatus.Replied;
    }

    public void OnAgentMarkedWaitingForCustomer(Case c, DateTimeOffset at) => Record(c, InquiryLifecycle.OnAgentMarkedWaitingForCustomer(c, at));

    public void OnAgentMarkedWaitingForInternal(Case c, DateTimeOffset at) => Record(c, InquiryLifecycle.OnAgentMarkedWaitingForInternal(c, at));

    public void OnReopenedByHand(Case c, DateTimeOffset at) => Record(c, InquiryLifecycle.OnReopenedByAgent(c, at));

    /// <summary>
    /// Completion from the CMS or the Agent (with a reason). Reasons that mean the concern was handled
    /// are a confirmed resolution; the rest close the inquiry for that stated reason. Either way,
    /// supervision ends and pending supervision reminders are cancelled.
    /// </summary>
    public async Task OnCompletedAsync(Case c, CaseCompletionReason reason, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var resolved = reason is CaseCompletionReason.CustomerRequestResolved or CaseCompletionReason.EmployeeResponded
            or CaseCompletionReason.PhoneCallHandled or CaseCompletionReason.HandledOutsideEmail;
        Record(c, resolved
            ? InquiryLifecycle.OnResolutionConfirmed(c, at, $"Confirmed resolved by the agent ({reason}).")
            : InquiryLifecycle.OnClosed(c, at, $"Closed: {reason}."));
        await CancelPendingSupervisionRemindersAsync(c.Id, cancellationToken);
    }

    /// <summary>Q2 - the agent confirms the inquiry is resolved (final authority). Ends all reminders and escalation.</summary>
    public async Task<Result<bool>> ConfirmResolvedAsync(Guid caseId, Guid? actorEmployeeId, string? comment, CancellationToken cancellationToken, DateTimeOffset? at = null)
    {
        var c = await _db.Cases.FirstOrDefaultAsync(x => x.Id == caseId, cancellationToken);
        if (c is null) return Result<bool>.Failure("Case not found.");
        if (InquiryLifecycle.IsTerminal(c.Stage)) return Result<bool>.Failure("This inquiry is already closed.");

        var now = at ?? DateTimeOffset.UtcNow;
        c.CompletionReason = CaseCompletionReason.CustomerRequestResolved;
        c.CompletionComment = string.IsNullOrWhiteSpace(comment) ? null : comment;
        c.CompletedAt = now;
        Record(c, InquiryLifecycle.OnResolutionConfirmed(c, now, "The agent confirmed the customer's concern is resolved."), actorEmployeeId);
        AddEvent(c, CaseEventType.Completed, $"Completed. Reason: {CaseCompletionReason.CustomerRequestResolved}.{(c.CompletionComment is null ? "" : $" Comment: {c.CompletionComment}")}", now, actorEmployeeId);
        await CancelPendingSupervisionRemindersAsync(c.Id, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return Result<bool>.Success(true);
    }

    /// <summary>Q2 - the agent says the AI was wrong: back to active supervision.</summary>
    public async Task<Result<bool>> RejectResolutionAsync(Guid caseId, Guid? actorEmployeeId, string? comment, CancellationToken cancellationToken, DateTimeOffset? at = null)
    {
        var c = await _db.Cases.FirstOrDefaultAsync(x => x.Id == caseId, cancellationToken);
        if (c is null) return Result<bool>.Failure("Case not found.");
        if (c.Stage != InquiryStage.PotentiallyResolved) return Result<bool>.Failure("This inquiry is not marked potentially resolved.");

        var transition = InquiryLifecycle.OnResolutionRejected(c, at ?? DateTimeOffset.UtcNow);
        Record(c, transition is null ? null : transition with
        {
            Reason = transition.Reason + (string.IsNullOrWhiteSpace(comment) ? "" : $" Agent's note: {comment}"),
        }, actorEmployeeId);
        await _db.SaveChangesAsync(cancellationToken);
        return Result<bool>.Success(true);
    }

    /// <summary>Phase 5 - the AI found the concern addressed: Potentially resolved, and the agent is asked to confirm.</summary>
    public async Task<bool> MarkPotentiallyResolvedAsync(Case c, string reason, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var transition = InquiryLifecycle.OnPotentiallyResolved(c, at, reason);
        if (transition is null) return false;
        Record(c, transition);
        await _db.SaveChangesAsync(cancellationToken);
        if (_notifications is not null)
        {
            await _notifications.SendForCaseAsync(NotificationType.ConfirmResolution, c, AgentPushCommandType.ShowCase, null, cancellationToken);
        }
        return true;
    }

    /// <summary>Reminders the old engine scheduled but never sent no longer apply once the stage changed.</summary>
    private async Task CancelPendingSupervisionRemindersAsync(Guid caseId, CancellationToken cancellationToken)
    {
        var pending = await _db.Reminders
            .Where(r => r.CaseId == caseId && r.Status == ReminderStatus.Scheduled && r.Trigger != ReminderTrigger.EmployeeRequested)
            .ToListAsync(cancellationToken);
        foreach (var r in pending)
        {
            r.Status = ReminderStatus.Cancelled;
            r.CancelReason = ReminderCancelReason.SupersededByNewerReminder;
            r.CancelDetail = "The inquiry moved to a new stage; supervision timers restarted.";
            r.CancelledAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private void Record(Case c, StageTransition? transition, Guid? actorEmployeeId = null)
    {
        if (transition is null) return;
        AddEvent(c, CaseEventType.StageChanged, transition.Describe(), transition.At, actorEmployeeId);
    }

    private void AddEvent(Case c, CaseEventType type, string detail, DateTimeOffset at, Guid? actorEmployeeId = null) =>
        _db.CaseEvents.Add(new CaseEvent { CaseId = c.Id, EventType = type, Detail = detail, OccurredAt = at, ActorEmployeeId = actorEmployeeId });
}
