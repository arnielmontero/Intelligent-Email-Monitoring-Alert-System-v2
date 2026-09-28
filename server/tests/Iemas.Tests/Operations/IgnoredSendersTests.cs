using Iemas.Application.Common.Interfaces;
using Iemas.Application.Common.Providers;
using Iemas.Application.EmailIntake;
using Iemas.Application.Escalations;
using Iemas.Application.Notifications;
using Iemas.Application.Operations;
using Iemas.Application.Reminders;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Iemas.Domain.Escalations;
using Iemas.Domain.Identity;
using Iemas.Domain.Notifications;
using Iemas.Domain.Operations;
using Iemas.Domain.Reminders;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iemas.Tests.Operations;

/// <summary>Email from an ignored domain/address must not reach classification, Cases, reminders, escalations or notifications.</summary>
public class IgnoredSendersTests
{
    private static async Task IgnoreAsync(TestDbContext db, string entries)
    {
        db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.IgnoredSenders, Value = entries });
        await db.SaveChangesAsync();
    }

    private static Case CreateCase(string customer, Guid? ownerId = null) => new()
    {
        CaseNumber = $"CASE-{Guid.NewGuid():N}"[..12], EmailAccountId = Guid.NewGuid(), CustomerEmailAddress = customer,
        Subject = "Order", NormalizedSubject = "Order", OwnerEmployeeId = ownerId ?? Guid.NewGuid(),
        WorkStatus = CaseWorkStatus.ActionRequired, ReplyStatus = CaseReplyStatus.AwaitingReply,
        FirstEmailReceivedAt = DateTimeOffset.UtcNow.AddDays(-3), LastActivityAt = DateTimeOffset.UtcNow,
    };

    [Theory]
    [InlineData("anna@sawo.com", true)]
    [InlineData("ANNA@Mail.Sawo.com", true)]
    [InlineData("anna@notsawo.com", false)]
    [InlineData("noreply@shop.example", true)]
    [InlineData("sales@shop.example", false)]
    [InlineData("customer@gmail.com", false)]
    public void Matches_DomainsSubdomainsAndExactAddresses(string sender, bool expected)
    {
        var list = IgnoredSenderList.Parse("sawo.com\nnoreply@shop.example");
        Assert.Equal(expected, list.Matches(sender));
    }

    [Fact]
    public void Normalize_AcceptsCommonFormats_AndRejectsGarbage()
    {
        var (normalized, error) = IgnoredSenderList.Normalize("@SAWO.com, *.news.example;  noreply@shop.example\nsawo.com");
        Assert.Null(error);
        Assert.Equal("news.example\nnoreply@shop.example\nsawo.com", normalized);

        Assert.NotNull(IgnoredSenderList.Normalize("not a domain").Error);
        Assert.NotNull(IgnoredSenderList.Normalize("user@").Error);
    }

    [Fact]
    public async Task Intake_StoresListedSenderAsIgnored()
    {
        using var db = TestDbContext.CreateNew();
        await IgnoreAsync(db, "sawo.com");
        var account = new EmailAccount
        {
            EmailAddress = "sales@company.example", Purpose = EmailAccountPurpose.Inbound, Protocol = EmailProtocol.Imap, Host = "imap.example.com",
            Port = 993, Encryption = "SSL/TLS", Username = "u", AuthMethod = EmailAuthMethod.Password, IsActive = true, MonitoringEnabled = true,
            Credential = new EmailCredential { EncryptedSecret = System.Text.Encoding.UTF8.GetBytes("p"), Nonce = Array.Empty<byte>(), Tag = Array.Empty<byte>(), KeyId = "t" },
        };
        db.EmailAccounts.Add(account);
        await db.SaveChangesAsync();
        ProviderMessage Msg(string id, string from) => new(id, $"<{id}@x>", null, null, Array.Empty<string>(), from, null,
            new[] { "sales@company.example" }, Array.Empty<string>(), "Hi", "Body", null, DateTimeOffset.UtcNow, Array.Empty<ProviderAttachmentSummary>());
        var adapter = new FakeEmailProviderAdapter
        {
            FetchBehavior = (_, _, _, _, _) => Task.FromResult(new FetchInboxResult(1, 2,
                new[] { Msg("1", "colleague@sawo.com"), Msg("2", "buyer@customer.example") }, Array.Empty<(uint, string)>())),
        };

        await new EmailIntakeService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter), NullLogger<EmailIntakeService>.Instance)
            .RunForAccountAsync(account.Id, CancellationToken.None);

        var byId = await db.EmailMessages.ToDictionaryAsync(m => m.ProviderMessageId, m => m.ProcessingStatus);
        Assert.Equal(EmailProcessingStatus.Ignored, byId["1"]);
        Assert.Equal(EmailProcessingStatus.PendingClassification, byId["2"]);
    }

    [Fact]
    public async Task SavingTheList_MarksWaitingEmailIgnored_AndRemovingRestoresIt()
    {
        using var db = TestDbContext.CreateNew();
        var account = new EmailAccount { EmailAddress = "sales@company.example", Host = "h", Username = "u", Encryption = "SSL/TLS" };
        db.EmailAccounts.Add(account);
        var waiting = new EmailMessage { EmailAccountId = account.Id, ProviderMessageId = "1", FromAddress = "x@sawo.com", Subject = "s", ReceivedAt = DateTimeOffset.UtcNow };
        db.EmailMessages.Add(waiting);
        await db.SaveChangesAsync();
        var audit = new NoOpAuditService();
        var settings = new SystemSettingsService(db, new FakeCurrentUserService(roles: SystemRole.Administrator), audit);

        var saved = await settings.UpdateAsync(new UpdateSystemSettingsRequest(new() { [SystemSettingKeys.IgnoredSenders] = "sawo.com" }), CancellationToken.None);
        Assert.True(saved.Succeeded, saved.Error);
        Assert.Equal(EmailProcessingStatus.Ignored, (await db.EmailMessages.AsNoTracking().SingleAsync()).ProcessingStatus);

        await settings.UpdateAsync(new UpdateSystemSettingsRequest(new() { [SystemSettingKeys.IgnoredSenders] = "" }), CancellationToken.None);
        Assert.Equal(EmailProcessingStatus.PendingClassification, (await db.EmailMessages.AsNoTracking().SingleAsync()).ProcessingStatus);
        Assert.Equal(2, audit.Entries.Count(e => e.Action == "IGNORED_SENDERS_APPLIED"));
    }

    [Fact]
    public async Task InvalidEntry_IsRejected()
    {
        using var db = TestDbContext.CreateNew();
        var settings = new SystemSettingsService(db, new FakeCurrentUserService(roles: SystemRole.Administrator), new NoOpAuditService());
        var result = await settings.UpdateAsync(new UpdateSystemSettingsRequest(new() { [SystemSettingKeys.IgnoredSenders] = "sawo" }), CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Contains("\"sawo\"", result.Error);
    }

    [Fact]
    public async Task ExistingCaseFromListedSender_GetsNoNotificationReminderOrEscalation()
    {
        using var db = TestDbContext.CreateNew();
        await IgnoreAsync(db, "sawo.com");
        var owner = new Employee { FullName = "Arniel", Email = "arniel@company.example" };
        db.Employees.Add(owner);
        var targetCase = CreateCase("colleague@sawo.com", owner.Id);
        db.Cases.Add(targetCase);
        var policy = new EscalationPolicy { Name = "Default", Enabled = true, IsDefault = true, TriggerReminderCount = 1, MaximumLevel = 1, Channel = "Email" };
        db.EscalationPolicies.Add(policy);
        db.EscalationLevels.Add(new EscalationLevel { EscalationPolicyId = policy.Id, Level = 1, RecipientType = EscalationRecipientType.Employee });
        db.Reminders.Add(new Reminder { CaseId = targetCase.Id, Trigger = ReminderTrigger.FollowUp, Status = ReminderStatus.Sent, SequenceNumber = 1, ScheduledForUtc = DateTimeOffset.UtcNow.AddHours(-2) });
        var due = new Reminder { CaseId = targetCase.Id, Trigger = ReminderTrigger.FollowUp, Status = ReminderStatus.Scheduled, SequenceNumber = 2, ScheduledForUtc = DateTimeOffset.UtcNow.AddMinutes(-1) };
        db.Reminders.Add(due);
        await db.SaveChangesAsync();
        var dispatcher = new FakeAgentNotificationDispatcher();
        var notifications = new NotificationService(db, dispatcher);

        var sent = await notifications.SendForCaseAsync(NotificationType.NewEmail, targetCase, AgentPushCommandType.ShowCase, null, CancellationToken.None);
        await new ReminderExecutionService(db, new ReminderSchedulingService(db), notifications).RunAsync(10, CancellationToken.None);
        var escalation = await new EscalationService(db, notifications).EvaluateCaseAsync(targetCase.Id, CancellationToken.None);

        Assert.Equal(NotificationSendOutcome.Suppressed, sent);
        Assert.Empty(dispatcher.Sent);
        Assert.False(await db.Notifications.AnyAsync());
        var reminder = await db.Reminders.AsNoTracking().SingleAsync(r => r.Id == due.Id);
        Assert.Equal(ReminderStatus.Cancelled, reminder.Status);
        Assert.Equal(ReminderCancelReason.SenderIgnored, reminder.CancelReason);
        Assert.Equal(EscalationOutcome.Skipped, escalation);
        Assert.Equal(CaseWorkStatus.ActionRequired, (await db.Cases.AsNoTracking().SingleAsync()).WorkStatus);
    }
}
