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
    private static InquiryFilterSettings DefaultSettings() => InquiryFilterSettings.Defaults("sawo.com");

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
    public void VendorRenewalPromotion_IsNotInquiry()
    {
        // Real case: a plugin vendor's marketing mail mentioning "pricing" was being turned into a Case.
        var result = Evaluate("Renew Before December 3 To Take Advantage Of Current Pricing",
            "Your license expires soon. Renew before December 3 to keep today's price. Are you ready to renew? Manage your subscription in your email preferences.",
            "list@wordfence.com");
        Assert.Equal(InquiryFilterVerdict.NotInquiry, result.Verdict);
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

    // ---- Learned from real inquiries (inquiries_export.csv) ----

    [Theory]
    [InlineData("test", "test")]
    [InlineData("TEEST", "TEST")]
    [InlineData("Test subject", "hello test")]
    [InlineData("Just Testing", "Test")]
    [InlineData("Makayat", "Test")]
    public void TestMessages_AreNotInquiries(string subject, string body)
    {
        Assert.Equal(InquiryFilterVerdict.NotInquiry, Evaluate(subject, body).Verdict);
    }

    [Fact]
    public void WordTest_InsideARealInquiry_IsNotATestMessage()
    {
        var result = Evaluate("Heater", "Does the latest model pass the test for UL? How much is it?");
        Assert.NotEqual(InquiryFilterVerdict.NotInquiry, result.Verdict);
    }

    [Theory]
    [InlineData("Attendees list - International Pool Spa Patio Expo 2026",
        "Would you like the Pool Spa Patio Expo 2026 Attendee list? If you are interested, please reply with \"Send counts and pricing.\"")]
    [InlineData("Pool & Spa Industry Database",
        "I'm reaching out to see if you'd be interested in accessing our verified Pool & Spa Industry Database. We can also provide customized, targeted lists.")]
    [InlineData("Pool Spa Patio Expo 2026 - Discounted Hotel Rooms for Exhibitors",
        "Greetings Exhibitors, Events Plus has negotiated a block of discounted hotel rooms in downtown New Orleans.")]
    public void TradeShowListVendors_AreNotInquiries(string subject, string body)
    {
        Assert.Equal(InquiryFilterVerdict.NotInquiry, Evaluate(subject, body).Verdict);
    }

    [Theory]
    [InlineData("Get A Wikipedia Page For Yourself Or For Your Company.", "Wikipedia is the World's most significant tool for reference material. Get a Wikipedia page today for a great price.")]
    [InlineData("Reliable Cable Supplier", "This is Ada Xu from Xinghua Cable, a professional manufacturer with ISO/CE/ROHS certified. Best price?")]
    public void ColdVendorPitches_AreNotInquiries(string subject, string body)
    {
        Assert.NotEqual(InquiryFilterVerdict.Inquiry, Evaluate(subject, body).Verdict);
    }

    [Theory]
    [InlineData("Customize My Sauna", "Would you be willing to provide a price quote? I didn't see a kW rating on the heater selected. Please send me the specification.")]
    [InlineData("Heater price", "I am highly interested for this model 12kw. Please provide the price (including shipping to Cyprus)?")]
    [InlineData("Where to buy?", "I am in Austria and do not know where I can buy the Scandia Fibercoated NB? Where to buy it?")]
    public void RealBuyerQuestions_AreNeverRejected(string subject, string body)
    {
        Assert.NotEqual(InquiryFilterVerdict.NotInquiry, Evaluate(subject, body).Verdict);
    }

    [Theory]
    [InlineData("Anfrage zum Preis Steuerteil", "Erbitte die Mitteilung des Preises für das Sauna-Steuerteil INNOVA Classic Built-In. Vielen Dank")]
    [InlineData("richiesta preventivo", "Buonasera, vorremmo un preventivo per una struttura.")]
    [InlineData("Conseil & devis", "Bonjour, je recherche un fournisseur pour mon projet. Pouvez-vous m'envoyer un devis ?")]
    [InlineData("ขอราคา Price list เครื่อง Steam Sawo", "รบกวนขอราคา Price list เครื่อง Steam Sawo")]
    public void NonEnglishPriceRequests_AreNotRejected(string subject, string body)
    {
        Assert.NotEqual(InquiryFilterVerdict.NotInquiry, Evaluate(subject, body).Verdict);
    }

    [Theory]
    // One-line website form messages that name a product: decided without AI.
    [InlineData("Need 6 kw sauna heater", "Need 6 kw sauna heater")]
    [InlineData("Customize My Sauna — Room: Standard Sauna Room - 1214RS - Straight Bench", "hi")]
    [InlineData("Quote request - SAWO SP02-200 - 50pcs", "SAWO SP02-200 - 50pcs")]
    [InlineData("Price for SW3-60NS-WL-P-C 6.0", "Price for this one with wifi control")]
    [InlineData("Timer in sauna", "We have a Finland sauna at our resort. There is no timer or temperature display in the sauna. Is this a requirement?")]
    [InlineData("Sauna - Ofen Händler in der Nähe", "Guten Tag, ich suche einen Ofenhändler in der Nähe. Mein Wohnort 90530 Wendelstein. Vielen Dank")]
    [InlineData("Ohjauspaneelin ja kiukaan välinen sarjaprotokolla", "Meiltä löytyy IKI 10kw kiuas ja siinä Sawo innova classic ohjauspaneeli. Miten ne on kytketty?")]
    public void ShortProductInquiries_AreInquiries_WithoutAi(string subject, string body)
    {
        var result = Evaluate(subject, body);
        Assert.Equal(InquiryFilterVerdict.Inquiry, result.Verdict);
        Assert.Equal(InquiryKind.Customer, result.Kind);
        Assert.Equal("Inquiry", result.Category);
    }

    [Theory]
    [InlineData("Dealer inquiry", "Hey, I'm running a retail store and we're looking to add more variety. We'd love to carry your sauna heaters.")]
    [InlineData("Reseller request", "Hi. Would like to start selling the SAWO product list on our website. I think the SAWO heaters will sell well. Can we become a reseller?")]
    [InlineData("Wholesale", "Hi, could you please send your wholesale catalog and price list? Thanks.")]
    [InlineData("Distribución", "Estamos interesados en una distribución para nuestro país")]
    public void DealerAndResellerRequests_AreLabelledDealer(string subject, string body)
    {
        var result = Evaluate(subject, body);
        Assert.Equal(InquiryFilterVerdict.Inquiry, result.Verdict);
        Assert.Equal(InquiryKind.Dealer, result.Kind);
        Assert.Equal("Dealer / Reseller", result.Category);
    }

    [Fact]
    public void CustomerLookingForALocalDealer_IsACustomerInquiry_NotADealer()
    {
        var result = Evaluate("Sawo heaters Bali", "Where can I buy Sawo sauna heaters in Bali please? Is there a dealer near me?");
        Assert.Equal(InquiryFilterVerdict.Inquiry, result.Verdict);
        Assert.Equal(InquiryKind.Customer, result.Kind);
    }

    [Theory]
    [InlineData("Product information for sourcing needs", "Hello, I would like to connect with your sourcing department - we specialize in producing aspen benches for sauna cooperation.")]
    [InlineData("Mitsuya Co., Ltd. - Plating Company", "I am writing from Mitsuya Co., Ltd., a Japan-based specialist in functional plating and surface treatment solutions for sauna heaters. Collaboration?")]
    [InlineData("Regarding Pool & Spa 2026 Attendees List", "Are you planning any marketing or prospecting campaigns around Pool & Spa 2026? We provide targeted contact data for sauna and spa manufacturers.")]
    public void VendorsSellingToUs_AreNeverAcceptedAsInquiries(string subject, string body)
    {
        Assert.NotEqual(InquiryFilterVerdict.Inquiry, Evaluate(subject, body).Verdict);
    }

    [Fact]
    public void EveryDefaultRuleList_FitsItsCatalogLimit_AndTheDatabaseColumn()
    {
        foreach (var d in SystemSettingsService.Catalog.Where(d => d.Type == SettingValueType.RuleList))
        {
            Assert.True(d.DefaultValue.Length <= (d.Max ?? int.MaxValue), $"{d.Key} default is {d.DefaultValue.Length} chars, catalog max {d.Max}.");
            Assert.True(d.DefaultValue.Length <= 20000, $"{d.Key} default does not fit system_settings.Value.");
        }
    }

    // The two live test emails sent on 2026-11-10 (the service offer re-sent here from an external address).
    [Fact]
    public void LiveTest_ServiceOffer_IsNotInquiry()
    {
        var result = Evaluate("Experience Relaxation with Our Professional Sauna Services",
            "Dear Valued Customer, Greetings! Are you looking for a relaxing way to unwind and take a break from your busy schedule? "
            + "We are pleased to introduce our professional sauna services, designed to provide a comfortable and relaxing wellness experience. "
            + "Our services include: Traditional sauna sessions, Infrared sauna sessions, Private sauna room reservations, Sauna packages for individuals and groups. "
            + "Whether you want to relax after a long day or enjoy a wellness experience, we are here to accommodate your needs. "
            + "Contact us today to learn more about our available packages, rates, and reservations. We look forward to serving you! Best regards,",
            "promo@sauna-spa.example");
        Assert.Equal(InquiryFilterVerdict.NotInquiry, result.Verdict);
    }

    [Fact]
    public void LiveTest_ProductSpecificationRequest_IsInquiry()
    {
        var result = Evaluate("Request for Sauna Product Specifications",
            "Dear Sales Team, Greetings! I recently came across your sauna products and would like to learn more about their features and specifications. "
            + "Could you provide details about the heating system, temperature settings, materials used, energy consumption, and warranty coverage? "
            + "I would appreciate receiving a brochure or product information sheet. Thank you, and I look forward to your reply. Kind regards,",
            "someone@gmail.example");
        Assert.Equal(InquiryFilterVerdict.Inquiry, result.Verdict);
        Assert.Equal(InquiryKind.Customer, result.Kind);
    }

    [Fact]
    public void ProductMentionWithoutAnyRequest_IsNotAConfidentInquiry()
    {
        // A product name alone (e.g. a forwarded spec line) has no intent: the AI decides.
        var result = Evaluate("Harvia Heizstab 1900W ZSA-430 f. Symphony, Forte", "Harvia Heizstab 1900W ZSA-430 f. Symphony, Forte");
        Assert.Equal(InquiryFilterVerdict.Uncertain, result.Verdict);
    }

    [Theory]
    [InlineData("Other", "Does this work?")]
    public void FormCheckMessages_AreTestMessages(string subject, string body)
    {
        Assert.Equal(InquiryFilterVerdict.NotInquiry, Evaluate(subject, body).Verdict);
    }

    [Fact]
    public void OftenIsNotTheGermanWordOfen()
    {
        var result = Evaluate("Question", "How often do you send updates?");
        Assert.DoesNotContain(result.Signals, s => s.Contains("ofen", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SparePartQuoteRequest_IsInquiry()
    {
        var result = Evaluate("spare part for NR-90NS-N",
            "Please provide the price and availability of a replacement part for the heater. Kindly send a quote with delivery time.");
        Assert.Equal(InquiryFilterVerdict.Inquiry, result.Verdict);
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
