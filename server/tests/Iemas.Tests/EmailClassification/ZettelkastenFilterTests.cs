using Iemas.Application.Common.Ai;
using Iemas.Application.EmailClassification;
using Iemas.Application.Operations;
using Iemas.Domain.Ai;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Iemas.Domain.Operations;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iemas.Tests.EmailClassification;

/// <summary>Phase 1: the Zettelkasten screening stage decides clear cases without AI; only uncertain email reaches the AI.</summary>
public class ZettelkastenFilterTests
{
    private static InquiryFilterSettings DefaultSettings() => new(
        IgnoredSenderList.Parse("sawo.com"),
        InquiryFilterSettings.Lines(SystemSettingDefaults.InquiryPhrases),
        InquiryFilterSettings.Lines(SystemSettingDefaults.NotInquiryPhrases),
        InquiryFilterSettings.Lines(SystemSettingDefaults.AutomatedSenderPatterns),
        InquiryThreshold: 5,
        NotInquiryThreshold: 3);

    private static InquiryFilterResult Evaluate(string subject, string body, string from = "buyer@customer.example", bool known = false) =>
        ZettelkastenFilter.Evaluate(new InquiryFilterInput(subject, body, from, known, false), DefaultSettings(), null);

    [Fact]
    public void ClearQuotationRequest_IsInquiry()
    {
        var result = Evaluate("Request for quotation", "Hello, I would like to request a quotation for 20 heaters. What is the price per unit?");
        Assert.Equal(InquiryFilterVerdict.Inquiry, result.Verdict);
        Assert.True(result.Score >= 5);
    }

    [Fact]
    public void Newsletter_IsNotInquiry()
    {
        var result = Evaluate("Our November newsletter", "Read our latest news. Unsubscribe at any time. View this email in your browser.", "newsletter@vendor.example");
        Assert.Equal(InquiryFilterVerdict.NotInquiry, result.Verdict);
    }

    [Fact]
    public void AutomatedPaymentNotice_IsNotInquiry()
    {
        var result = Evaluate("Payment confirmation", "Payment received for invoice 1043. This is an automated message.", "noreply@bank.example");
        Assert.Equal(InquiryFilterVerdict.NotInquiry, result.Verdict);
    }

    [Fact]
    public void ImplicitInterestWithoutKeywords_IsUncertain_SoAiDecides()
    {
        // Module 4's own example: a keyword filter cannot be sure; context is the AI's job.
        var result = Evaluate("Upcoming project", "I visited your website and wanted to know whether your team could support us on an upcoming project.");
        Assert.Equal(InquiryFilterVerdict.Uncertain, result.Verdict);
    }

    [Fact]
    public void MixedSignals_AreNeverDecidedWithoutAi()
    {
        // Inquiry phrases plus a not-an-inquiry phrase: context matters, so the AI decides.
        var result = Evaluate("Pricing", "Could you send your price list? How much is shipping? (sent from our webinar signup)");
        Assert.Equal(InquiryFilterVerdict.Uncertain, result.Verdict);
    }

    [Fact]
    public void InternalDomainSender_NeverStartsAnInquiry_EvenWithInquiryWords()
    {
        var result = Evaluate("Quote for customer", "Can you provide a quotation for ACME? How much?", "colleague@sawo.com");
        Assert.Equal(InquiryFilterVerdict.NotInquiry, result.Verdict);
        Assert.Contains("internal", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InternalSubdomainSender_IsInternalToo()
    {
        var result = Evaluate("Pricing?", "how much?", "it@mail.sawo.com");
        Assert.Equal(InquiryFilterVerdict.NotInquiry, result.Verdict);
    }

    [Fact]
    public void KnownCustomerAskingAQuestion_ReachesInquiryWithFewerPhrases()
    {
        var unknown = Evaluate("Availability", "Do you have the 6kW model in stock?");
        var known = Evaluate("Availability", "Do you have the 6kW model in stock?", known: true);
        Assert.True(known.Score > unknown.Score);
        Assert.Equal(InquiryFilterVerdict.Inquiry, known.Verdict);
    }

    [Fact]
    public void ProfileExclude_StaysDecisive()
    {
        var profile = new ClassificationProfile { Name = "Sales", Enabled = true, ExcludeDefinitions = "shipped" };
        var result = ZettelkastenFilter.Evaluate(
            new InquiryFilterInput("Your order has shipped - how much more?", "Request a quotation", "buyer@customer.example", false, false),
            DefaultSettings(), profile);
        Assert.Equal(InquiryFilterVerdict.NotInquiry, result.Verdict);
    }

    // ---- Through EmailClassificationService: verifies the AI is called only for Uncertain ----

    private static (TestDbContext Db, EmailAccount Account) Seed()
    {
        var db = TestDbContext.CreateNew();
        var account = new EmailAccount
        {
            EmailAddress = "agent1@sawo.com", Purpose = EmailAccountPurpose.Inbound, Protocol = EmailProtocol.Imap,
            Host = "imap.example.com", Port = 993, Encryption = "SSL/TLS", Username = "agent1@sawo.com",
            AuthMethod = EmailAuthMethod.Password, IsActive = true, MonitoringEnabled = true,
        };
        db.EmailAccounts.Add(account);
        db.AiModelConfigs.Add(new AiModelConfig
        {
            Provider = "OpenRouter", ModelIdentifier = "m", DisplayName = "m", Enabled = true,
            TaskCapability = "EmailClassification", TimeoutSeconds = 30,
        });
        db.SaveChanges();
        return (db, account);
    }

    private static EmailMessage Message(Guid accountId, string from, string subject, string body) => new()
    {
        EmailAccountId = accountId, Provider = EmailProtocol.Imap, ProviderMessageId = Guid.NewGuid().ToString(),
        FromAddress = from, ToAddresses = "agent1@sawo.com", Subject = subject, BodyText = body,
        ReceivedAt = DateTimeOffset.UtcNow, ProcessingStatus = EmailProcessingStatus.PendingClassification,
    };

    private static EmailClassificationService Service(TestDbContext db, FakeAiClassificationProvider ai) =>
        new(db, ai, new NoOpRetryDelay(), new Iemas.Application.Common.Ai.AiCircuitBreakerStore(TimeProvider.System), NullLogger<EmailClassificationService>.Instance);

    [Fact]
    public async Task ConfidentInquiry_BecomesImportant_WithoutCallingAi()
    {
        var (db, account) = Seed();
        var message = Message(account.Id, "buyer@customer.example", "Request for quotation",
            "I would like to request a quotation for 20 heaters. How much per unit?");
        db.EmailMessages.Add(message);
        await db.SaveChangesAsync();
        var ai = new FakeAiClassificationProvider();

        var decision = await Service(db, ai).ClassifyOneAsync(message.Id, CancellationToken.None);

        Assert.Equal(ImportanceDecision.Important, decision);
        Assert.Empty(ai.CallsByModel);
        var classification = await db.EmailClassifications.SingleAsync();
        Assert.StartsWith("Zettelkasten: Inquiry", classification.DeterministicFilterReason);
    }

    [Fact]
    public async Task Newsletter_BecomesNotImportant_WithoutCallingAi()
    {
        var (db, account) = Seed();
        var message = Message(account.Id, "newsletter@vendor.example", "News", "Unsubscribe here. View this email in your browser.");
        db.EmailMessages.Add(message);
        await db.SaveChangesAsync();
        var ai = new FakeAiClassificationProvider();

        var decision = await Service(db, ai).ClassifyOneAsync(message.Id, CancellationToken.None);

        Assert.Equal(ImportanceDecision.NotImportant, decision);
        Assert.Empty(ai.CallsByModel);
    }

    [Fact]
    public async Task UncertainEmail_IsSentToAi_AndAiDecides()
    {
        var (db, account) = Seed();
        var message = Message(account.Id, "someone@company.example", "Upcoming project",
            "I visited your website and wanted to know whether your team could support us on an upcoming project.");
        db.EmailMessages.Add(message);
        await db.SaveChangesAsync();
        var ai = new FakeAiClassificationProvider();

        var decision = await Service(db, ai).ClassifyOneAsync(message.Id, CancellationToken.None);

        Assert.Single(ai.CallsByModel);
        Assert.Equal(ImportanceDecision.Important, decision);
    }

    [Fact]
    public async Task OwnMonitoredMailboxAsSender_NeverStartsAnInquiry()
    {
        var (db, account) = Seed();
        var message = Message(account.Id, "agent1@sawo.com", "Re: quotation", "How much? Request a quotation please.");
        db.EmailMessages.Add(message);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.InternalDomains, Value = "" });
        await db.SaveChangesAsync();
        var ai = new FakeAiClassificationProvider();

        var decision = await Service(db, ai).ClassifyOneAsync(message.Id, CancellationToken.None);

        Assert.Equal(ImportanceDecision.NotImportant, decision);
        Assert.Empty(ai.CallsByModel);
    }

    [Fact]
    public async Task InternalDomainList_IsConfigurable()
    {
        var (db, account) = Seed();
        db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.InternalDomains, Value = "sawo.com\nsawo-group.example" });
        var message = Message(account.Id, "ops@sawo-group.example", "Quote for customer", "Request a quotation, how much?");
        db.EmailMessages.Add(message);
        await db.SaveChangesAsync();
        var ai = new FakeAiClassificationProvider();

        var decision = await Service(db, ai).ClassifyOneAsync(message.Id, CancellationToken.None);

        Assert.Equal(ImportanceDecision.NotImportant, decision);
    }
}
