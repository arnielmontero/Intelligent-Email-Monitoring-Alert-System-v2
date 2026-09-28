using Iemas.Application.Common.Providers;
using Iemas.Application.EmailAccounts;
using Iemas.Application.EmailAccounts.Dtos;
using Iemas.Application.EmailIntake;
using Iemas.Domain.Email;
using Iemas.Infrastructure.Providers;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iemas.Tests.EmailIntake;

/// <summary>Mailbox history from before monitoring started is stored but never turned into Cases.</summary>
public class EmailCutoffTests
{
    private static readonly DateTimeOffset Cutoff = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static EmailAccount CreateAccount(DateTimeOffset? cutoff) => new()
    {
        EmailAddress = "sales@sawo.com", Purpose = EmailAccountPurpose.Inbound, Protocol = EmailProtocol.Imap,
        Host = "imap.example.com", Port = 993, Encryption = "SSL/TLS", Username = "sales@sawo.com",
        AuthMethod = EmailAuthMethod.Password, IsActive = true, MonitoringEnabled = true, ProcessEmailsReceivedAfter = cutoff,
        Credential = new EmailCredential { EncryptedSecret = System.Text.Encoding.UTF8.GetBytes("password"), Nonce = Array.Empty<byte>(), Tag = Array.Empty<byte>(), KeyId = "test" },
    };

    private static ProviderMessage Message(string id, DateTimeOffset receivedAt) => new(
        id, $"<{id}@example.com>", null, null, Array.Empty<string>(), "customer@example.com", "Customer",
        new[] { "sales@sawo.com" }, Array.Empty<string>(), "Subject", "Body", null, receivedAt, Array.Empty<ProviderAttachmentSummary>());

    [Fact]
    public async Task Intake_EmailBeforeCutoff_IsStoredAsHistorical()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount(Cutoff);
        db.EmailAccounts.Add(account);
        await db.SaveChangesAsync();
        var adapter = new FakeEmailProviderAdapter
        {
            FetchBehavior = (_, _, _, _, _) => Task.FromResult(new FetchInboxResult(1, 2,
                new[] { Message("1", Cutoff.AddDays(-300)), Message("2", Cutoff.AddHours(1)) }, Array.Empty<(uint, string)>())),
        };
        var service = new EmailIntakeService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter), NullLogger<EmailIntakeService>.Instance);

        await service.RunForAccountAsync(account.Id, CancellationToken.None);

        var messages = await db.EmailMessages.OrderBy(m => m.ProviderMessageId).ToListAsync();
        Assert.Equal(EmailProcessingStatus.Historical, messages[0].ProcessingStatus);
        Assert.Equal(EmailProcessingStatus.PendingClassification, messages[1].ProcessingStatus);
    }

    [Fact]
    public async Task UpdateCutoff_MovesWaitingEmailBothWays_AndAudits()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount(null);
        db.EmailAccounts.Add(account);
        var old = new EmailMessage { EmailAccountId = account.Id, ProviderMessageId = "1", FromAddress = "c@example.com", Subject = "old", ReceivedAt = Cutoff.AddDays(-30), ProcessingStatus = EmailProcessingStatus.PendingClassification };
        var recent = new EmailMessage { EmailAccountId = account.Id, ProviderMessageId = "2", FromAddress = "c@example.com", Subject = "new", ReceivedAt = Cutoff.AddDays(1), ProcessingStatus = EmailProcessingStatus.PendingClassification };
        var done = new EmailMessage { EmailAccountId = account.Id, ProviderMessageId = "3", FromAddress = "c@example.com", Subject = "done", ReceivedAt = Cutoff.AddDays(-60), ProcessingStatus = EmailProcessingStatus.Processed };
        db.EmailMessages.AddRange(old, recent, done);
        await db.SaveChangesAsync();
        var audit = new NoOpAuditService();
        var service = new EmailAccountService(db, new PassThroughEncryptionService(), new EmailProviderAdapterResolver(Array.Empty<IEmailProviderAdapter>()), audit);

        UpdateEmailAccountRequest Request(DateTimeOffset? cutoff) => new(null, EmailAccountKind.Primary, "imap.example.com", 993, "SSL/TLS", "sales@sawo.com",
            EmailAuthMethod.Password, null, null, null, true, true, cutoff);

        Assert.True((await service.UpdateAsync(account.Id, Request(Cutoff), CancellationToken.None)).Succeeded);
        Assert.Equal(EmailProcessingStatus.Historical, (await db.EmailMessages.AsNoTracking().SingleAsync(m => m.Id == old.Id)).ProcessingStatus);
        Assert.Equal(EmailProcessingStatus.PendingClassification, (await db.EmailMessages.AsNoTracking().SingleAsync(m => m.Id == recent.Id)).ProcessingStatus);
        Assert.Equal(EmailProcessingStatus.Processed, (await db.EmailMessages.AsNoTracking().SingleAsync(m => m.Id == done.Id)).ProcessingStatus);

        Assert.True((await service.UpdateAsync(account.Id, Request(null), CancellationToken.None)).Succeeded);
        Assert.Equal(EmailProcessingStatus.PendingClassification, (await db.EmailMessages.AsNoTracking().SingleAsync(m => m.Id == old.Id)).ProcessingStatus);
        Assert.Equal(2, audit.Entries.Count(e => e.Action == "EMAIL_ACCOUNT_CUTOFF_CHANGED"));
    }

    /// <summary>The cut-off is handed to the provider so mailbox history is skipped at the source instead of queuing ahead of new mail.</summary>
    [Fact]
    public async Task Intake_PassesCutoffToProvider()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount(Cutoff);
        db.EmailAccounts.Add(account);
        await db.SaveChangesAsync();
        DateTimeOffset? passed = null;
        var adapter = new CutoffCapturingAdapter(d => passed = d);
        var service = new EmailIntakeService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter), NullLogger<EmailIntakeService>.Instance);

        await service.RunForAccountAsync(account.Id, CancellationToken.None);

        Assert.Equal(Cutoff, passed);
    }

    private sealed class CutoffCapturingAdapter : IEmailProviderAdapter
    {
        private readonly Action<DateTimeOffset?> _capture;
        public CutoffCapturingAdapter(Action<DateTimeOffset?> capture) => _capture = capture;
        public EmailProtocol Protocol => EmailProtocol.Imap;
        public Task<ProviderConnectionTestResult> TestConnectionAsync(EmailProviderConnectionSettings settings, CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderConnectionTestResult(true, null, TimeSpan.Zero));
        public Task<FetchInboxResult> FetchInboxMessagesAsync(EmailProviderConnectionSettings settings, uint? knownUidValidity, uint? afterUid, int maxMessages, CancellationToken cancellationToken, DateTimeOffset? deliveredAfter = null)
        {
            _capture(deliveredAfter);
            return Task.FromResult(new FetchInboxResult(1, afterUid, Array.Empty<ProviderMessage>(), Array.Empty<(uint, string)>()));
        }
        public Task<FetchSentResult> FetchSentMessagesAsync(EmailProviderConnectionSettings settings, DateTimeOffset since, int maxMessages, CancellationToken cancellationToken) =>
            Task.FromResult(new FetchSentResult(true, null, Array.Empty<ProviderMessage>(), Array.Empty<(string, string)>()));
    }
}
