using System.Text;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Inquiries;
using Iemas.Application.Notifications;
using Iemas.Domain.Cases;

namespace Iemas.Application.Escalations;

/// <summary>What management needs to understand an escalated inquiry (Module 11's required list).</summary>
public sealed record EscalationEmailContext(
    Case Case,
    string? AgentName,
    string? AgentEmail,
    int Level,
    string Reason,
    IReadOnlyList<DateTimeOffset> RemindersSent,
    TimeZoneInfo Zone,
    DateTimeOffset Now);

/// <summary>
/// Module 11 - the escalation email. Contains: customer, original subject and date, assigned agent,
/// current status, whether the agent has responded, last conversation activity, how long it has
/// been inactive/unresolved, previous reminders and the reason for escalation. Plain text so it
/// reads the same in every mail client; never includes the conversation body itself.
/// </summary>
public static class EscalationEmailComposer
{
    public static OutboundEmail Compose(EscalationEmailContext x, IReadOnlyList<EmailRecipient> to)
    {
        var c = x.Case;
        string Local(DateTimeOffset t) => $"{TimeZoneInfo.ConvertTime(t, x.Zone):yyyy-MM-dd HH:mm} ({x.Zone.Id})";

        var lastActivity = new[] { c.LastCustomerMessageAt, c.LastAgentMessageAt, c.LastInternalActivityAt }
            .Where(t => t is not null).Select(t => t!.Value).DefaultIfEmpty(c.FirstEmailReceivedAt).Max();
        var lastActivityBy = lastActivity == c.LastAgentMessageAt ? "agent" : lastActivity == c.LastCustomerMessageAt ? "customer" : "internal";

        var body = new StringBuilder()
            .AppendLine($"An inquiry needs management attention (escalation level {x.Level}).")
            .AppendLine()
            .AppendLine($"Reason for escalation: {x.Reason}")
            .AppendLine()
            .AppendLine($"Inquiry:            {c.CaseNumber}")
            .AppendLine($"Customer:           {(string.IsNullOrWhiteSpace(c.CustomerDisplayName) ? c.CustomerEmailAddress : $"{c.CustomerDisplayName} <{c.CustomerEmailAddress}>")}")
            .AppendLine($"Original subject:   {c.Subject}")
            .AppendLine($"Original date:      {Local(c.FirstEmailReceivedAt)}")
            .AppendLine($"Assigned agent:     {(x.AgentName is null ? "(unassigned)" : $"{x.AgentName} <{x.AgentEmail}>")}")
            .AppendLine($"Current status:     {InquiryLifecycle.Label(c.Stage)}{(c.OverdueSince is null ? "" : " - OVERDUE")}")
            .AppendLine($"Agent has replied:  {(c.FirstAgentResponseAt is DateTimeOffset first ? $"Yes, first reply {Local(first)}" : "No")}")
            .AppendLine($"Last activity:      {Local(lastActivity)} (by {lastActivityBy})")
            .AppendLine($"Inactive for:       {NotificationService.FormatElapsed(x.Now - lastActivity)}")
            .AppendLine($"Open for:           {NotificationService.FormatElapsed(x.Now - (c.SupervisionCycleStartedAt ?? c.FirstEmailReceivedAt))}")
            .AppendLine($"Resolution target:  {(c.ResolutionDueAt is DateTimeOffset due ? Local(due) : "-")}")
            .AppendLine($"Previous reminders: {(x.RemindersSent.Count == 0 ? "none" : $"{x.RemindersSent.Count} ({string.Join(", ", x.RemindersSent.Select(Local))})")}")
            .AppendLine()
            .AppendLine("Open the inquiry in the IEMAS CMS (Cases) for the full conversation and history.")
            .AppendLine("This is an internal message from the inquiry monitoring system.");

        return new OutboundEmail(to, $"[Escalation L{x.Level}] {c.CaseNumber}: {c.Subject}", body.ToString());
    }
}
