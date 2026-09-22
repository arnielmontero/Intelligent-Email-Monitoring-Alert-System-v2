using Iemas.Application.Common.Ai;
using Iemas.Application.EmailClassification;
using Iemas.Domain.Ai;
using Iemas.Domain.Email;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iemas.Tests.EmailClassification;

public class EmailClassificationServiceTests
{
    private static EmailAccount CreateAccount(string? profileName = "Sales")
    {
        return new EmailAccount
        {
            EmailAddress = "sales@sawo.com",
            Purpose = EmailAccountPurpose.Inbound,
            Protocol = EmailProtocol.Imap,
            Host = "imap.example.com",
            Port = 993,
            Encryption = "SSL/TLS",
            Username = "sales@sawo.com",
            AuthMethod = EmailAuthMethod.Password,
            IsActive = true,
            MonitoringEnabled = true,
            ClassificationProfileName = profileName,
        };
    }

    private static ClassificationProfile CreateSalesProfile()
    {
        return new ClassificationProfile
        {
            Name = "Sales",
            Enabled = true,
            Categories = "PRODUCT_INQUIRY\nPRICE_REQUEST",
            IncludeDefinitions = "price\nquotation\ninquiry",
            ExcludeDefinitions = "newsletter\nunsubscribe\nshipped",
        };
    }

    private static EmailMessage CreateMessage(Guid accountId, string subject, string? body)
    {
        return new EmailMessage
        {
            EmailAccountId = accountId,
            Provider = EmailProtocol.Imap,
            ProviderMessageId = Guid.NewGuid().ToString(),
            FromAddress = "customer@example.com",
            ToAddresses = "sales@sawo.com",
            Subject = subject,
            BodyText = body,
            ReceivedAt = DateTimeOffset.UtcNow,
            ProcessingStatus = EmailProcessingStatus.PendingClassification,
        };
    }

    private static AiModelConfig CreateModel(string identifier = "openai/gpt-4o-mini", int fallbackOrder = 0, int maxRetries = 0)
    {
        return new AiModelConfig
        {
            Provider = "OpenRouter",
            ModelIdentifier = identifier,
            DisplayName = identifier,
            Enabled = true,
            TaskCapability = "EmailClassification",
            TimeoutSeconds = 30,
            MaxRetries = maxRetries,
            FallbackOrder = fallbackOrder,
        };
    }

    /// <summary>Important message: high-confidence relevant AI result must produce Important + Processed.</summary>
    [Fact]
    public async Task ClassifyOneAsync_HighConfidenceRelevant_ProducesImportantDecision()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        var profile = CreateSalesProfile();
        db.EmailAccounts.Add(account);
        db.ClassificationProfiles.Add(profile);
        db.AiModelConfigs.Add(CreateModel());
        var message = CreateMessage(account.Id, "Request for ABC Product Pricing", "Customer wants 50 units and requests latest price and availability.");
        db.EmailMessages.Add(message);
        await db.SaveChangesAsync();

        var provider = new FakeAiClassificationProvider
        {
            Behavior = (_, model, _, _) => Task.FromResult(new ClassificationAttemptResult(
                true, "OpenRouter", model,
                new ClassificationResponse(true, "PRODUCT_INQUIRY", true, true, "HIGH", 0.96, "Customer requesting pricing."),
                null, 42))
        };
        var service = new EmailClassificationService(db, provider);

        var decision = await service.ClassifyOneAsync(message.Id, CancellationToken.None);

        Assert.Equal(ImportanceDecision.Important, decision);
        var reloaded = await db.EmailMessages.SingleAsync(m => m.Id == message.Id);
        Assert.Equal(EmailProcessingStatus.Processed, reloaded.ProcessingStatus);
        Assert.Equal("PRODUCT_INQUIRY", reloaded.Classification);
        Assert.Equal(0.96, reloaded.AiConfidence);

        var classification = await db.EmailClassifications.SingleAsync(c => c.EmailMessageId == message.Id);
        Assert.Equal(ImportanceDecision.Important, classification.Decision);
        Assert.Equal(ConfidenceBand.High, classification.ConfidenceBand);
        Assert.True(classification.DeterministicFilterMatched);
        Assert.Equal(profile.Id, classification.ClassificationProfileId);
    }

    /// <summary>Non-important: confident NOT_RELEVANT AI result must produce NotImportant + Processed, message still stored (§33).</summary>
    [Fact]
    public async Task ClassifyOneAsync_ConfidentNotRelevant_ProducesNotImportantDecision_AndMessageIsStillStored()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        var profile = CreateSalesProfile();
        db.EmailAccounts.Add(account);
        db.ClassificationProfiles.Add(profile);
        db.AiModelConfigs.Add(CreateModel());
        var message = CreateMessage(account.Id, "Your Lazada Order Has Shipped", "Your package has shipped and is on its way.");
        db.EmailMessages.Add(message);
        await db.SaveChangesAsync();

        var provider = new FakeAiClassificationProvider
        {
            Behavior = (_, model, _, _) => Task.FromResult(new ClassificationAttemptResult(
                true, "OpenRouter", model,
                new ClassificationResponse(false, "ECOMMERCE_NOTIFICATION", false, false, "LOW", 0.9, "Automated shipping notification."),
                null, 20))
        };
        var service = new EmailClassificationService(db, provider);

        var decision = await service.ClassifyOneAsync(message.Id, CancellationToken.None);

        Assert.Equal(ImportanceDecision.NotImportant, decision);
        var reloaded = await db.EmailMessages.SingleAsync(m => m.Id == message.Id);
        Assert.Equal(EmailProcessingStatus.Processed, reloaded.ProcessingStatus);
        // §33 — non-relevant emails are still stored/classified, never deleted.
        Assert.NotNull(await db.EmailMessages.FindAsync(message.Id));
    }

    /// <summary>
    /// The build instructions specifically require testing "messages that superficially match a
    /// keyword but should be rejected after content analysis" — a message contains an include
    /// keyword ("price") but the AI still determines it is not relevant (e.g. an automated price
    /// change notice, not a genuine customer inquiry). The deterministic filter forwards it to AI
    /// (as it should — it cannot make the final call), and the AI's semantic judgement wins.
    /// </summary>
    [Fact]
    public async Task ClassifyOneAsync_SuperficialKeywordMatch_StillRejectedAfterAiContentAnalysis()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        var profile = CreateSalesProfile();
        db.EmailAccounts.Add(account);
        db.ClassificationProfiles.Add(profile);
        db.AiModelConfigs.Add(CreateModel());
        // Contains "price" (an include term) but is really an automated notification, not a genuine inquiry.
        var message = CreateMessage(account.Id, "Your subscription price has changed", "This is an automated notice that your monthly subscription price will increase next cycle. No action is required.");
        db.EmailMessages.Add(message);
        await db.SaveChangesAsync();

        var provider = new FakeAiClassificationProvider
        {
            Behavior = (_, model, _, _) => Task.FromResult(new ClassificationAttemptResult(
                true, "OpenRouter", model,
                new ClassificationResponse(false, "AUTOMATED_NOTIFICATION", false, false, "LOW", 0.88, "Automated billing notice, not a genuine customer inquiry."),
                null, 15))
        };
        var service = new EmailClassificationService(db, provider);

        var decision = await service.ClassifyOneAsync(message.Id, CancellationToken.None);

        // Deterministic filter matched the "price" include term and correctly deferred to AI
        // rather than declaring it Important itself; AI's content analysis is what rejected it.
        var classification = await db.EmailClassifications.SingleAsync(c => c.EmailMessageId == message.Id);
        Assert.True(classification.DeterministicFilterMatched);
        Assert.Contains("price", classification.DeterministicFilterReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ImportanceDecision.NotImportant, decision);
    }

    /// <summary>Deterministic exclude match short-circuits without ever calling the AI provider (cost control, §27/§28).</summary>
    [Fact]
    public async Task ClassifyOneAsync_DeterministicExcludeMatch_SkipsAiEntirely()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        var profile = CreateSalesProfile();
        db.EmailAccounts.Add(account);
        db.ClassificationProfiles.Add(profile);
        db.AiModelConfigs.Add(CreateModel());
        var message = CreateMessage(account.Id, "Weekly Newsletter", "Unsubscribe here if you no longer wish to receive our newsletter.");
        db.EmailMessages.Add(message);
        await db.SaveChangesAsync();

        var provider = new FakeAiClassificationProvider();
        var service = new EmailClassificationService(db, provider);

        var decision = await service.ClassifyOneAsync(message.Id, CancellationToken.None);

        Assert.Equal(ImportanceDecision.NotImportant, decision);
        Assert.Empty(provider.CallsByModel);

        var log = await db.AiClassificationLogs.SingleAsync(l => l.EmailMessageId == message.Id);
        Assert.Equal(AiClassificationOutcome.SkippedByDeterministicFilter, log.Outcome);
    }

    /// <summary>§83 — provider failure exhausting retries must not lose/fail the message; it goes to ReviewRequired, non-fatal.</summary>
    [Fact]
    public async Task ClassifyOneAsync_ProviderFailsEveryAttempt_ProducesReviewRequired_MessageNotLost()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        var profile = CreateSalesProfile();
        db.EmailAccounts.Add(account);
        db.ClassificationProfiles.Add(profile);
        db.AiModelConfigs.Add(CreateModel(maxRetries: 1));
        var message = CreateMessage(account.Id, "Price inquiry", "Please send your latest price list.");
        db.EmailMessages.Add(message);
        await db.SaveChangesAsync();

        var provider = new FakeAiClassificationProvider
        {
            Behavior = (_, model, _, _) => Task.FromResult(new ClassificationAttemptResult(
                false, "OpenRouter", model, null, "OpenRouter returned HTTP 503: Service Unavailable", 5))
        };
        var service = new EmailClassificationService(db, provider);

        var decision = await service.ClassifyOneAsync(message.Id, CancellationToken.None);

        Assert.Equal(ImportanceDecision.ReviewRequired, decision);
        // maxRetries=1 → 2 attempts total (initial + 1 retry) against the one configured model.
        Assert.Equal(2, provider.CallsByModel.Count);

        var reloaded = await db.EmailMessages.SingleAsync(m => m.Id == message.Id);
        Assert.Equal(EmailProcessingStatus.ReviewRequired, reloaded.ProcessingStatus);
        Assert.Contains("503", reloaded.ProcessingError);
        // The message row itself is untouched/not duplicated — intake is never re-run or lost.
        Assert.Equal(1, await db.EmailMessages.CountAsync());

        var failureLog = await db.AiClassificationLogs.SingleAsync(l => l.Outcome == AiClassificationOutcome.ProviderFailed);
        Assert.Contains("503", failureLog.Detail);
    }

    /// <summary>§83 fallback — when the primary model fails, a lower-priority fallback model must still be tried and can succeed.</summary>
    [Fact]
    public async Task ClassifyOneAsync_PrimaryModelFails_FallsBackToSecondModel_AndSucceeds()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        var profile = CreateSalesProfile();
        db.EmailAccounts.Add(account);
        db.ClassificationProfiles.Add(profile);
        db.AiModelConfigs.Add(CreateModel("primary-model", fallbackOrder: 0, maxRetries: 0));
        db.AiModelConfigs.Add(CreateModel("fallback-model", fallbackOrder: 1, maxRetries: 0));
        var message = CreateMessage(account.Id, "Price inquiry", "Please send your latest price list.");
        db.EmailMessages.Add(message);
        await db.SaveChangesAsync();

        var provider = new FakeAiClassificationProvider
        {
            Behavior = (_, model, _, _) => model == "primary-model"
                ? Task.FromResult(new ClassificationAttemptResult(false, "OpenRouter", model, null, "primary down", 5))
                : Task.FromResult(new ClassificationAttemptResult(
                    true, "OpenRouter", model,
                    new ClassificationResponse(true, "PRICE_REQUEST", true, true, "HIGH", 0.9, "Pricing request."),
                    null, 12))
        };
        var service = new EmailClassificationService(db, provider);

        var decision = await service.ClassifyOneAsync(message.Id, CancellationToken.None);

        Assert.Equal(ImportanceDecision.Important, decision);
        Assert.Equal(new[] { "primary-model", "fallback-model" }, provider.CallsByModel);

        var classification = await db.EmailClassifications.SingleAsync(c => c.EmailMessageId == message.Id);
        Assert.Equal("fallback-model", classification.AiModel);
    }

    /// <summary>§83 — a malformed/unrecognizable AI response (surfaced by the provider as a failed attempt) must not crash and must go to ReviewRequired.</summary>
    [Fact]
    public async Task ClassifyOneAsync_MalformedAiResponse_ProducesReviewRequired()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        var profile = CreateSalesProfile();
        db.EmailAccounts.Add(account);
        db.ClassificationProfiles.Add(profile);
        db.AiModelConfigs.Add(CreateModel(maxRetries: 0));
        var message = CreateMessage(account.Id, "Price inquiry", "Please send your latest price list.");
        db.EmailMessages.Add(message);
        await db.SaveChangesAsync();

        var provider = new FakeAiClassificationProvider
        {
            Behavior = (_, model, _, _) => Task.FromResult(new ClassificationAttemptResult(
                false, "OpenRouter", model, null,
                "Could not parse a valid classification from the model response: Missing or non-numeric 'confidence' field.", 8))
        };
        var service = new EmailClassificationService(db, provider);

        var decision = await service.ClassifyOneAsync(message.Id, CancellationToken.None);

        Assert.Equal(ImportanceDecision.ReviewRequired, decision);
        var reloaded = await db.EmailMessages.SingleAsync(m => m.Id == message.Id);
        Assert.Equal(EmailProcessingStatus.ReviewRequired, reloaded.ProcessingStatus);
        Assert.Contains("Could not parse", reloaded.ProcessingError);
    }

    /// <summary>§26/§83 — a low-confidence AI result must go to ReviewRequired, not be treated as a trusted decision either way.</summary>
    [Fact]
    public async Task ClassifyOneAsync_LowConfidence_ProducesReviewRequired()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        var profile = CreateSalesProfile();
        db.EmailAccounts.Add(account);
        db.ClassificationProfiles.Add(profile);
        db.AiModelConfigs.Add(CreateModel());
        var message = CreateMessage(account.Id, "Price inquiry", "Please send your latest price list.");
        db.EmailMessages.Add(message);
        await db.SaveChangesAsync();

        var provider = new FakeAiClassificationProvider
        {
            Behavior = (_, model, _, _) => Task.FromResult(new ClassificationAttemptResult(
                true, "OpenRouter", model,
                new ClassificationResponse(true, "PRICE_REQUEST", true, true, "MEDIUM", 0.3, "Uncertain."),
                null, 10))
        };
        var service = new EmailClassificationService(db, provider);

        var decision = await service.ClassifyOneAsync(message.Id, CancellationToken.None);

        Assert.Equal(ImportanceDecision.ReviewRequired, decision);
        var reloaded = await db.EmailMessages.SingleAsync(m => m.Id == message.Id);
        Assert.Equal(EmailProcessingStatus.ReviewRequired, reloaded.ProcessingStatus);

        var classification = await db.EmailClassifications.SingleAsync(c => c.EmailMessageId == message.Id);
        Assert.Equal(ConfidenceBand.Low, classification.ConfidenceBand);
    }

    /// <summary>§82 — no enabled model configured must not silently drop the message; goes to ReviewRequired with a clear reason.</summary>
    [Fact]
    public async Task ClassifyOneAsync_NoModelConfigured_ProducesReviewRequired()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        var message = CreateMessage(account.Id, "Price inquiry", "Please send your latest price list.");
        db.EmailMessages.Add(message);
        await db.SaveChangesAsync();

        var provider = new FakeAiClassificationProvider();
        var service = new EmailClassificationService(db, provider);

        var decision = await service.ClassifyOneAsync(message.Id, CancellationToken.None);

        Assert.Equal(ImportanceDecision.ReviewRequired, decision);
        Assert.Empty(provider.CallsByModel);
        var reloaded = await db.EmailMessages.SingleAsync(m => m.Id == message.Id);
        Assert.Contains("No enabled AI model", reloaded.ProcessingError);
    }

    /// <summary>Re-check pattern (matches EmailIntakeServiceTests style) — a message no longer PendingClassification must be skipped quietly, not reprocessed.</summary>
    [Fact]
    public async Task ClassifyOneAsync_SkipsQuietly_WhenMessageIsNoLongerPendingClassification()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        var message = CreateMessage(account.Id, "Price inquiry", "Please send your latest price list.");
        message.ProcessingStatus = EmailProcessingStatus.Processed;
        db.EmailMessages.Add(message);
        await db.SaveChangesAsync();

        var provider = new FakeAiClassificationProvider();
        var service = new EmailClassificationService(db, provider);

        var decision = await service.ClassifyOneAsync(message.Id, CancellationToken.None);

        Assert.Null(decision);
        Assert.Empty(provider.CallsByModel);
    }

    /// <summary>RunAsync batches multiple pending messages and tallies the decision counts (mirrors RunAllAsync's per-account tally in intake).</summary>
    [Fact]
    public async Task RunAsync_ClassifiesBatchOfPendingMessages_AndTalliesDecisions()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        var profile = CreateSalesProfile();
        db.EmailAccounts.Add(account);
        db.ClassificationProfiles.Add(profile);
        db.AiModelConfigs.Add(CreateModel());

        var important = CreateMessage(account.Id, "Price inquiry", "Please send your latest price list.");
        var notImportant = CreateMessage(account.Id, "Weekly Newsletter", "Unsubscribe here.");
        db.EmailMessages.AddRange(important, notImportant);
        await db.SaveChangesAsync();

        var provider = new FakeAiClassificationProvider
        {
            Behavior = (_, model, _, _) => Task.FromResult(new ClassificationAttemptResult(
                true, "OpenRouter", model,
                new ClassificationResponse(true, "PRICE_REQUEST", true, true, "HIGH", 0.92, "Pricing request."),
                null, 10))
        };
        var service = new EmailClassificationService(db, provider);

        var result = await service.RunAsync(10, CancellationToken.None);

        Assert.Equal(2, result.ConsideredCount);
        Assert.Equal(1, result.ImportantCount);
        Assert.Equal(1, result.NotImportantCount);
        // Newsletter never reached the AI provider (deterministic exclude short-circuit).
        Assert.Single(provider.CallsByModel);
    }
}
