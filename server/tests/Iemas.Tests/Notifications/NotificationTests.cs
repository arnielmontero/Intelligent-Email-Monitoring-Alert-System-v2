using Iemas.Application.Common.Interfaces;
using Iemas.Application.Escalations;
using Iemas.Application.Notifications;
using Iemas.Application.Reminders;
using Iemas.Domain.Ai;
using Iemas.Domain.Cases;
using Iemas.Domain.Escalations;
using Iemas.Domain.Identity;
using Iemas.Domain.Notifications;
using Iemas.Domain.Operations;
using Iemas.Domain.Reminders;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Tests.Notifications;

public class NotificationTests
{
    private static Case CreateCase(Guid? ownerEmployeeId) => new()
    {
        CaseNumber = "CASE-000123",
        EmailAccountId = Guid.NewGuid(),
        CustomerEmailAddress = "maria@customer.example",
        CustomerDisplayName = "Maria Santos",
        Subject = "Quotation for 20 units",
        NormalizedSubject = "Quotation for 20 units",
        OwnerEmployeeId = ownerEmployeeId,
        WorkStatus = CaseWorkStatus.ActionRequired,
        ReplyStatus = CaseReplyStatus.AwaitingReply,
        FirstEmailReceivedAt = DateTimeOffset.UtcNow.AddHours(-3),
        LastActivityAt = DateTimeOffset.UtcNow,
    };

    private static NotificationAdminService CreateAdmin(TestDbContext db, NoOpAuditService? audit = null) =>
        new(db, new FakeCurrentUserService(roles: SystemRole.Administrator), audit ?? new NoOpAuditService());

    /// <summary>§51 — the stored, CMS-edited template drives the message; every supported variable is filled.</summary>
    [Fact]
    public async Task SendForCaseAsync_UsesEditedTemplateAndFillsVariables()
    {
        using var db = TestDbContext.CreateNew();
        var owner = new Employee { FullName = "John", Email = "john@sawo.com" };
        db.Employees.Add(owner);
        var targetCase = CreateCase(owner.Id);
        db.Cases.Add(targetCase);
        db.NotificationTemplates.Add(new NotificationTemplate
        {
            Type = NotificationType.NewEmail, Enabled = true, Title = "New: {{case_id}}",
            MessageText = "Hi {{employee_name}}: {{sender_name}} <> {{sender_email}} re {{email_subject}}, {{reminder_count}} reminders, waiting {{elapsed_time}}",
        });
        await db.SaveChangesAsync();

        var dispatcher = new FakeAgentNotificationDispatcher();
        var outcome = await new NotificationService(db, dispatcher).SendForCaseAsync(
            NotificationType.NewEmail, targetCase, AgentPushCommandType.ShowCase, null, CancellationToken.None);

        Assert.Equal(NotificationSendOutcome.Sent, outcome);
        var push = Assert.Single(dispatcher.Sent);
        Assert.Equal(owner.Id, push.EmployeeId);
        Assert.Equal("New: CASE-000123", push.Command.Title);
        Assert.Equal("Hi John: Maria Santos <> maria@customer.example re Quotation for 20 units, 0 reminders, waiting 3h 0m", push.Command.Message);

        var notification = await db.Notifications.SingleAsync();
        Assert.Equal(NotificationDeliveryStatus.Sent, notification.Status);
        Assert.Equal(1, notification.DeliveredAgentCount);
        Assert.NotNull(notification.SentAt);
    }

    [Fact]
    public async Task SendForCaseAsync_NoTemplateRow_FallsBackToDefault()
    {
        using var db = TestDbContext.CreateNew();
        var owner = new Employee { FullName = "John", Email = "john@sawo.com" };
        db.Employees.Add(owner);
        var targetCase = CreateCase(owner.Id);
        db.Cases.Add(targetCase);
        await db.SaveChangesAsync();

        var dispatcher = new FakeAgentNotificationDispatcher();
        await new NotificationService(db, dispatcher).SendForCaseAsync(
            NotificationType.NewEmail, targetCase, AgentPushCommandType.ShowCase, null, CancellationToken.None);

        Assert.StartsWith("Hey John, you have a new email received", Assert.Single(dispatcher.Sent).Command.Message);
    }

    [Fact]
    public async Task SendForCaseAsync_DisabledTemplate_SendsNothing()
    {
        using var db = TestDbContext.CreateNew();
        var targetCase = CreateCase(Guid.NewGuid());
        db.Cases.Add(targetCase);
        var disabled = NotificationTemplateDefaults.For(NotificationType.NewEmail);
        disabled.Enabled = false;
        db.NotificationTemplates.Add(disabled);
        await db.SaveChangesAsync();

        var dispatcher = new FakeAgentNotificationDispatcher();
        var outcome = await new NotificationService(db, dispatcher).SendForCaseAsync(
            NotificationType.NewEmail, targetCase, AgentPushCommandType.ShowCase, null, CancellationToken.None);

        Assert.Equal(NotificationSendOutcome.TemplateDisabled, outcome);
        Assert.Empty(dispatcher.Sent);
        Assert.False(await db.Notifications.AnyAsync());
    }

    /// <summary>§104 — with no Agent online the notification is honestly Queued, not Sent.</summary>
    [Fact]
    public async Task SendForCaseAsync_NoConnectedAgent_IsQueued()
    {
        using var db = TestDbContext.CreateNew();
        var targetCase = CreateCase(Guid.NewGuid());
        db.Cases.Add(targetCase);
        await db.SaveChangesAsync();

        var dispatcher = new FakeAgentNotificationDispatcher { ConnectedAgentCount = 0 };
        var outcome = await new NotificationService(db, dispatcher).SendForCaseAsync(
            NotificationType.NewEmail, targetCase, AgentPushCommandType.ShowCase, null, CancellationToken.None);

        Assert.Equal(NotificationSendOutcome.Queued, outcome);
        var notification = await db.Notifications.SingleAsync();
        Assert.Equal(NotificationDeliveryStatus.Queued, notification.Status);
        Assert.Null(notification.SentAt);
    }

    [Fact]
    public async Task SendForCaseAsync_CaseWithoutOwner_HasNoRecipient()
    {
        using var db = TestDbContext.CreateNew();
        var outcome = await new NotificationService(db, new FakeAgentNotificationDispatcher()).SendForCaseAsync(
            NotificationType.NewEmail, CreateCase(null), AgentPushCommandType.ShowCase, null, CancellationToken.None);
        Assert.Equal(NotificationSendOutcome.NoRecipient, outcome);
    }

    [Fact]
    public async Task AcknowledgeForCaseAsync_AcknowledgesOnlyOpenNotificationsForThatCase()
    {
        using var db = TestDbContext.CreateNew();
        var employeeId = Guid.NewGuid();
        var caseId = Guid.NewGuid();
        db.Notifications.AddRange(
            new Notification { CaseId = caseId, EmployeeId = employeeId, Status = NotificationDeliveryStatus.Sent, Title = "t", Message = "m" },
            new Notification { CaseId = caseId, EmployeeId = employeeId, Status = NotificationDeliveryStatus.Queued, Title = "t", Message = "m" },
            new Notification { CaseId = caseId, EmployeeId = employeeId, Status = NotificationDeliveryStatus.Cancelled, Title = "t", Message = "m" },
            new Notification { CaseId = Guid.NewGuid(), EmployeeId = employeeId, Status = NotificationDeliveryStatus.Sent, Title = "t", Message = "m" });
        await db.SaveChangesAsync();

        var count = await NotificationService.AcknowledgeForCaseAsync(db, caseId, employeeId, CancellationToken.None);

        Assert.Equal(2, count);
        Assert.Equal(2, await db.Notifications.CountAsync(n => n.Status == NotificationDeliveryStatus.Acknowledged && n.AcknowledgedAt != null));
    }

    [Fact]
    public async Task UpdateTemplateAsync_UnknownVariable_IsRejected()
    {
        using var db = TestDbContext.CreateNew();
        var result = await CreateAdmin(db).UpdateTemplateAsync("Reminder",
            new UpdateNotificationTemplateRequest(true, "Reminder", "Hey {{employe_name}}"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("{{employe_name}}", result.Error);
    }

    [Fact]
    public async Task UpdateTemplateAsync_Markup_IsRejected()
    {
        using var db = TestDbContext.CreateNew();
        var result = await CreateAdmin(db).UpdateTemplateAsync("Reminder",
            new UpdateNotificationTemplateRequest(true, "Reminder", "<script>x</script>"), CancellationToken.None);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task UpdateThenReset_IsAuditedAndRestoresDefault()
    {
        using var db = TestDbContext.CreateNew();
        var audit = new NoOpAuditService();
        var admin = CreateAdmin(db, audit);

        var updated = await admin.UpdateTemplateAsync("Overdue",
            new UpdateNotificationTemplateRequest(false, "Late!", "{{case_id}} is overdue"), CancellationToken.None);
        Assert.True(updated.Succeeded);
        Assert.True(updated.Value!.IsCustomized);

        var reset = await admin.ResetTemplateAsync("Overdue", CancellationToken.None);
        Assert.False(reset.Value!.IsCustomized);
        Assert.Equal(NotificationTemplateDefaults.For(NotificationType.Overdue).MessageText, reset.Value.MessageText);

        Assert.Equal(new[] { "NOTIFICATION_TEMPLATE_UPDATED", "NOTIFICATION_TEMPLATE_RESET" }, audit.Entries.Select(e => e.Action));
        var templates = await admin.GetTemplatesAsync(CancellationToken.None);
        Assert.Equal(6, templates.Templates.Count);
    }

    /// <summary>§52 — the reminder that reaches the escalation policy's trigger count warns the employee instead of a plain reminder.</summary>
    [Fact]
    public async Task ReminderEngine_ReminderReachingEscalationThreshold_SendsEscalationWarning()
    {
        using var db = TestDbContext.CreateNew();
        var owner = new Employee { FullName = "John", Email = "john@sawo.com" };
        db.Employees.Add(owner);
        db.EscalationPolicies.Add(new EscalationPolicy
        {
            Name = "Default", Enabled = true, IsDefault = true, TriggerReminderCount = 2,
            GracePeriod = TimeSpan.Zero, Cooldown = TimeSpan.Zero, MaximumLevel = 1, Channel = "Email",
        });
        var targetCase = CreateCase(owner.Id);
        db.Cases.Add(targetCase);
        db.Reminders.Add(new Reminder
        {
            CaseId = targetCase.Id, Trigger = ReminderTrigger.InitialActionRequired, Status = ReminderStatus.Sent,
            SequenceNumber = 1, ScheduledForUtc = DateTimeOffset.UtcNow.AddHours(-2),
        });
        db.Reminders.Add(new Reminder
        {
            CaseId = targetCase.Id, Trigger = ReminderTrigger.FollowUp, Status = ReminderStatus.Scheduled,
            SequenceNumber = 2, ScheduledForUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
        });
        await db.SaveChangesAsync();

        var dispatcher = new FakeAgentNotificationDispatcher();
        await new ReminderExecutionService(db, new ReminderSchedulingService(db), new NotificationService(db, dispatcher))
            .RunAsync(10, CancellationToken.None);

        var notification = await db.Notifications.SingleAsync();
        Assert.Equal(NotificationType.EscalationWarning, notification.Type);
        Assert.Contains("after 2 reminder(s)", notification.Message);
        Assert.Equal(AgentPushCommandType.ShowReminder, Assert.Single(dispatcher.Sent).Command.Type);
    }

    [Fact]
    public async Task ReminderEngine_FirstReminder_UsesFirstReminderTemplate()
    {
        using var db = TestDbContext.CreateNew();
        var targetCase = CreateCase(Guid.NewGuid());
        db.Cases.Add(targetCase);
        db.Reminders.Add(new Reminder
        {
            CaseId = targetCase.Id, Trigger = ReminderTrigger.InitialActionRequired, Status = ReminderStatus.Scheduled,
            SequenceNumber = 1, ScheduledForUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
        });
        await db.SaveChangesAsync();

        await new ReminderExecutionService(db, new ReminderSchedulingService(db), new NotificationService(db, new FakeAgentNotificationDispatcher()))
            .RunAsync(10, CancellationToken.None);

        Assert.Equal(NotificationType.FirstReminder, (await db.Notifications.SingleAsync()).Type);
    }

    [Fact]
    public async Task EscalationEngine_LevelExecuted_NotifiesOwner()
    {
        using var db = TestDbContext.CreateNew();
        var owner = new Employee { FullName = "John", Email = "john@sawo.com" };
        db.Employees.Add(owner);
        var policy = new EscalationPolicy
        {
            Name = "Default", Enabled = true, IsDefault = true, TriggerReminderCount = 1,
            GracePeriod = TimeSpan.Zero, Cooldown = TimeSpan.Zero, MaximumLevel = 1, Channel = "Email",
        };
        db.EscalationPolicies.Add(policy);
        db.EscalationLevels.Add(new EscalationLevel { EscalationPolicyId = policy.Id, Level = 1, RecipientType = EscalationRecipientType.Employee });
        var targetCase = CreateCase(owner.Id);
        db.Cases.Add(targetCase);
        db.Reminders.Add(new Reminder
        {
            CaseId = targetCase.Id, Trigger = ReminderTrigger.InitialActionRequired, Status = ReminderStatus.Sent,
            SequenceNumber = 1, ScheduledForUtc = DateTimeOffset.UtcNow.AddHours(-1),
        });
        await db.SaveChangesAsync();

        var dispatcher = new FakeAgentNotificationDispatcher();
        var outcome = await new EscalationService(db, new NotificationService(db, dispatcher)).EvaluateCaseAsync(targetCase.Id, CancellationToken.None);

        Assert.Equal(EscalationOutcome.Executed, outcome);
        var notification = await db.Notifications.SingleAsync();
        Assert.Equal(NotificationType.Escalation, notification.Type);
        Assert.Equal(owner.Id, notification.EmployeeId);
    }

    [Fact]
    public async Task SendForCaseAsync_ReceivedAt_UsesConfiguredTimeZone()
    {
        using var db = TestDbContext.CreateNew();
        var targetCase = CreateCase(Guid.NewGuid());
        targetCase.FirstEmailReceivedAt = new DateTimeOffset(2026, 1, 15, 0, 15, 0, TimeSpan.Zero);
        db.Cases.Add(targetCase);
        db.SystemSettings.Add(new SystemSetting { Key = "general.default_time_zone", Value = "Asia/Manila" });
        db.NotificationTemplates.Add(new NotificationTemplate { Type = NotificationType.NewEmail, Enabled = true, Title = "t", MessageText = "{{received_at}}" });
        await db.SaveChangesAsync();

        await new NotificationService(db).SendForCaseAsync(NotificationType.NewEmail, targetCase, AgentPushCommandType.ShowCase, null, CancellationToken.None);

        Assert.Equal("2026-01-15 08:15 (Asia/Manila)", (await db.Notifications.SingleAsync()).Message);
    }
}
