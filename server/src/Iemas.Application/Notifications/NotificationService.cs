using System.Text.RegularExpressions;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Operations;
using Iemas.Domain.Cases;
using Iemas.Domain.Notifications;
using Iemas.Domain.Operations;
using Iemas.Domain.Reminders;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Notifications;

public enum NotificationSendOutcome
{
    Sent,
    Queued,
    Suppressed,
    TemplateDisabled,
    NoRecipient,
}

public static class NotificationTemplateDefaults
{
    public static readonly IReadOnlyList<string> SupportedVariables = new[]
    {
        "employee_name", "email_subject", "sender_name", "sender_email", "received_at",
        "ai_summary", "elapsed_time", "reminder_count", "case_id",
    };

    /// <summary>Requirements §52 example wording.</summary>
    public static NotificationTemplate For(NotificationType type) => type switch
    {
        NotificationType.NewEmail => Create(type, "New Email",
            "Hey {{employee_name}}, you have a new email received from {{sender_email}}: \"{{email_subject}}\". Please check."),
        NotificationType.FirstReminder => Create(type, "Reminder",
            "Hey {{employee_name}}, have you replied already to the email you received last time? (\"{{email_subject}}\" from {{sender_email}})"),
        NotificationType.Reminder => Create(type, "Reminder",
            "Hey {{employee_name}}, you haven't replied to the email \"{{email_subject}}\" from {{sender_email}}. Please reply."),
        NotificationType.Overdue => Create(type, "Overdue",
            "Overdue: \"{{email_subject}}\" from {{sender_name}} ({{sender_email}}), received {{received_at}}, outstanding for {{elapsed_time}}. Summary: {{ai_summary}}"),
        NotificationType.EscalationWarning => Create(type, "Escalation Warning",
            "Hey {{employee_name}}, \"{{email_subject}}\" ({{case_id}}) is still unresolved after {{reminder_count}} reminder(s). It will be escalated if it remains unresolved."),
        NotificationType.Escalation => Create(type, "Escalated",
            "\"{{email_subject}}\" ({{case_id}}) has been escalated after {{reminder_count}} reminder(s) without a verified reply."),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static NotificationTemplate Create(NotificationType type, string title, string message) =>
        new() { Type = type, Enabled = true, Title = title, MessageText = message };
}

/// <summary>
/// Requirements §51-§53, §104 — renders the editable template for a notification, records it
/// (committed before any push, per §76) and hands it to the Agent transport. A notification is a
/// record that something happened, never Action Required work in itself (§53).
/// </summary>
public class NotificationService
{
    private static readonly Regex VariablePattern = new(@"\{\{\s*([a-z_]+)\s*\}\}", RegexOptions.Compiled);

    private readonly IAppDbContext _db;
    private readonly IAgentNotificationDispatcher? _dispatcher;

    public NotificationService(IAppDbContext db, IAgentNotificationDispatcher? dispatcher = null)
    {
        _db = db;
        _dispatcher = dispatcher;
    }

    public async Task<NotificationSendOutcome> SendForCaseAsync(
        NotificationType type, Case targetCase, AgentPushCommandType pushType, int? reminderCount, CancellationToken cancellationToken)
    {
        if (targetCase.OwnerEmployeeId is not Guid employeeId)
        {
            return NotificationSendOutcome.NoRecipient;
        }

        if ((await IgnoredSenderList.LoadAsync(_db, cancellationToken)).Matches(targetCase.CustomerEmailAddress))
        {
            return NotificationSendOutcome.Suppressed;
        }

        var template = await _db.NotificationTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Type == type, cancellationToken)
            ?? NotificationTemplateDefaults.For(type);
        if (!template.Enabled)
        {
            return NotificationSendOutcome.TemplateDisabled;
        }

        var variables = await BuildVariablesAsync(targetCase, employeeId, reminderCount, cancellationToken);
        var notification = new Notification
        {
            CaseId = targetCase.Id,
            EmployeeId = employeeId,
            Type = type,
            Title = Render(template.Title, variables),
            Message = Render(template.MessageText, variables),
            Status = NotificationDeliveryStatus.Scheduled,
        };
        _db.Notifications.Add(notification);

        if (await _db.IsPausedAsync(PauseControl.AgentNotifications, cancellationToken))
        {
            notification.Status = NotificationDeliveryStatus.Cancelled;
            notification.FailureReason = "Suppressed: Agent Notifications are paused (Emergency Pause).";
            await _db.SaveChangesAsync(cancellationToken);
            return NotificationSendOutcome.Suppressed;
        }

        await _db.SaveChangesAsync(cancellationToken);

        var delivered = _dispatcher is null
            ? 0
            : await _dispatcher.NotifyEmployeeAsync(
                employeeId,
                new AgentPushCommand(pushType, targetCase.Id, targetCase.CaseNumber, notification.Title, notification.Message),
                cancellationToken);

        notification.DeliveredAgentCount = delivered;
        notification.Status = delivered > 0 ? NotificationDeliveryStatus.Sent : NotificationDeliveryStatus.Queued;
        notification.SentAt = delivered > 0 ? DateTimeOffset.UtcNow : null;
        notification.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return delivered > 0 ? NotificationSendOutcome.Sent : NotificationSendOutcome.Queued;
    }

    /// <summary>§104 ACKNOWLEDGED — the employee acknowledged the Case, so every still-open notification for it is acknowledged.</summary>
    public static async Task<int> AcknowledgeForCaseAsync(IAppDbContext db, Guid caseId, Guid employeeId, CancellationToken cancellationToken)
    {
        var open = await db.Notifications
            .Where(n => n.CaseId == caseId && n.EmployeeId == employeeId
                        && (n.Status == NotificationDeliveryStatus.Sent || n.Status == NotificationDeliveryStatus.Queued
                            || n.Status == NotificationDeliveryStatus.Delivered || n.Status == NotificationDeliveryStatus.Displayed))
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        foreach (var notification in open)
        {
            notification.Status = NotificationDeliveryStatus.Acknowledged;
            notification.AcknowledgedAt = now;
            notification.UpdatedAt = now;
        }

        if (open.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        return open.Count;
    }

    /// <summary>Unknown variables are left untouched so a typo stays visible rather than silently disappearing.</summary>
    public static string Render(string text, IReadOnlyDictionary<string, string> variables) =>
        VariablePattern.Replace(text, m => variables.TryGetValue(m.Groups[1].Value, out var value) ? value : m.Value);

    public static IReadOnlyList<string> FindUnknownVariables(string text) =>
        VariablePattern.Matches(text)
            .Select(m => m.Groups[1].Value)
            .Where(name => !NotificationTemplateDefaults.SupportedVariables.Contains(name))
            .Distinct()
            .ToList();

    public static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed.TotalDays >= 1) return $"{(int)elapsed.TotalDays}d {elapsed.Hours}h";
        if (elapsed.TotalHours >= 1) return $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m";
        return $"{Math.Max(0, (int)elapsed.TotalMinutes)}m";
    }

    private async Task<Dictionary<string, string>> BuildVariablesAsync(Case targetCase, Guid employeeId, int? reminderCount, CancellationToken cancellationToken)
    {
        var employeeName = await _db.Employees.AsNoTracking()
            .Where(e => e.Id == employeeId)
            .Select(e => e.FullName)
            .FirstOrDefaultAsync(cancellationToken);

        var caseMessageIds = _db.CaseEmails.Where(ce => ce.CaseId == targetCase.Id).Select(ce => ce.EmailMessageId);
        var summary = await _db.EmailClassifications.AsNoTracking()
            .Where(c => caseMessageIds.Contains(c.EmailMessageId) && c.Summary != null)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => c.Summary)
            .FirstOrDefaultAsync(cancellationToken);

        var count = reminderCount
            ?? await _db.Reminders.CountAsync(r => r.CaseId == targetCase.Id && r.Status == ReminderStatus.Sent, cancellationToken);

        var zone = await SystemSettingsService.GetDefaultTimeZoneAsync(_db, cancellationToken);
        var receivedLocal = TimeZoneInfo.ConvertTime(targetCase.FirstEmailReceivedAt, zone);

        return new Dictionary<string, string>
        {
            ["employee_name"] = employeeName ?? "there",
            ["email_subject"] = targetCase.Subject,
            ["sender_name"] = string.IsNullOrWhiteSpace(targetCase.CustomerDisplayName) ? targetCase.CustomerEmailAddress : targetCase.CustomerDisplayName,
            ["sender_email"] = targetCase.CustomerEmailAddress,
            ["received_at"] = $"{receivedLocal:yyyy-MM-dd HH:mm} ({zone.Id})",
            ["ai_summary"] = summary ?? "(no summary available)",
            ["elapsed_time"] = FormatElapsed(DateTimeOffset.UtcNow - targetCase.FirstEmailReceivedAt),
            ["reminder_count"] = count.ToString(),
            ["case_id"] = targetCase.CaseNumber,
        };
    }
}
