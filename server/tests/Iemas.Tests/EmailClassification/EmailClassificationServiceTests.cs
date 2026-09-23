using Iemas.Application.Common.Ai;
using Iemas.Application.EmailClassification;
using Iemas.Domain.Ai;
using Iemas.Domain.Email;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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
        var service = new EmailClassificationService(db, provider, new NoOpRetryDelay(), new AiCircuitBreakerStore(TimeProvider.System), NullLogger<EmailClassificationService>.Instance);

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
        var service = new EmailClassificationService(db, provider, new NoOpRetryDelay(), new AiCircuitBreakerStore(TimeProvider.System), NullLogger<EmailClassificationService>.Instance);

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
        var service = new EmailClassificationService(db, provider, new NoOpRetryDelay(), new AiCircuitBreakerStore(TimeProvider.System), NullLogger<EmailClassificationService>.Instance);

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
        var service = new EmailClassificationService(db, provider, new NoOpRetryDelay(), new AiCircuitBreakerStore(TimeProvider.System), NullLogger<EmailClassificationService>.Instance);

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
            // Phase 10: a 503 is Transient — the real OpenRouterClassificationProvider would
            // categorize it the same way (see CategorizeHttpFailure), so this is worth retrying.
            Behavior = (_, model, _, _) => Task.FromResult(new ClassificationAttemptResult(
                false, "OpenRouter", model, null, "OpenRouter returned HTTP 503: Service Unavailable", 5,
                ClassificationFailureCategory.Transient))
        };
        var service = new EmailClassificationService(db, provider, new NoOpRetryDelay(), new AiCircuitBreakerStore(TimeProvider.System), NullLogger<EmailClassificationService>.Instance);

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

    /// <summary>
    /// Phase 10 hardening — a failure category the next attempt cannot fix (AuthenticationFailure:
    /// wrong/missing API key) must not burn the configured retry budget on the same model; it should
    /// move straight to a fallback model instead, since retrying it produces the identical failure
    /// every time.
    /// </summary>
    [Fact]
    public async Task ClassifyOneAsync_AuthenticationFailure_DoesNotRetrySameModel_FallsBackImmediately()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        var profile = CreateSalesProfile();
        db.EmailAccounts.Add(account);
        db.ClassificationProfiles.Add(profile);
        db.AiModelConfigs.Add(CreateModel("primary-model", fallbackOrder: 0, maxRetries: 3));
        db.AiModelConfigs.Add(CreateModel("fallback-model", fallbackOrder: 1, maxRetries: 0));
        var message = CreateMessage(account.Id, "Price inquiry", "Please send your latest price list.");
        db.EmailMessages.Add(message);
        await db.SaveChangesAsync();

        var provider = new FakeAiClassificationProvider
        {
            Behavior = (_, model, _, _) => Task.FromResult(model == "primary-model"
                ? new ClassificationAttemptResult(false, "OpenRouter", model, null, "OpenRouter API key is not configured.", 1, ClassificationFailureCategory.AuthenticationFailure)
                : new ClassificationAttemptResult(true, "OpenRouter", model,
                    new ClassificationResponse(true, "PRODUCT_INQUIRY", true, true, "HIGH", 0.9, "Pricing question."), null, 10))
        };
        var delay = new NoOpRetryDelay();
        var service = new EmailClassificationService(db, provider, delay, new AiCircuitBreakerStore(TimeProvider.System), NullLogger<EmailClassificationService>.Instance);

        var decision = await service.ClassifyOneAsync(message.Id, CancellationToken.None);

        Assert.Equal(ImportanceDecision.Important, decision);
        // maxRetries=3 configured on primary-model, but AuthenticationFailure must not be retried —
        // exactly 1 call to primary-model, then straight to fallback-model, never a wasted retry.
        Assert.Equal(new[] { "primary-model", "fallback-model" }, provider.CallsByModel);
        Assert.Empty(delay.RequestedDelays);
    }

    /// <summary>
    /// Phase 10 hardening — a malformed/unparseable AI response gets exactly one retry (the model
    /// might have glitched once), not the full retry budget, since a model systematically returning
    /// bad JSON would otherwise burn every retry attempt repeating the identical parse failure.
    /// </summary>
    [Fact]
    public async Task ClassifyOneAsync_MalformedResponse_RetriesExactlyOnce_ThenFallsBack()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        var profile = CreateSalesProfile();
        db.EmailAccounts.Add(account);
        db.ClassificationProfiles.Add(profile);
        db.AiModelConfigs.Add(CreateModel("primary-model", fallbackOrder: 0, maxRetries: 3));
        db.AiModelConfigs.Add(CreateModel("fallback-model", fallbackOrder: 1, maxRetries: 0));
        var message = CreateMessage(account.Id, "Price inquiry", "Please send your latest price list.");
        db.EmailMessages.Add(message);
        await db.SaveChangesAsync();

        var provider = new FakeAiClassificationProvider
        {
            Behavior = (_, model, _, _) => Task.FromResult(model == "primary-model"
                ? new ClassificationAttemptResult(false, "OpenRouter", model, null, "Could not parse a valid classification from the model response.", 1, ClassificationFailureCategory.MalformedResponse)
                : new ClassificationAttemptResult(true, "OpenRouter", model,
                    new ClassificationResponse(true, "PRODUCT_INQUIRY", true, true, "HIGH", 0.9, "Pricing question."), null, 10))
        };
        var service = new EmailClassificationService(db, provider, new NoOpRetryDelay(), new AiCircuitBreakerStore(TimeProvider.System), NullLogger<EmailClassificationService>.Instance);

        var decision = await service.ClassifyOneAsync(message.Id, CancellationToken.None);

        Assert.Equal(ImportanceDecision.Important, decision);
        // maxRetries=3 configured, but MalformedResponse only ever gets 1 retry (2 total calls to
        // primary-model: the initial attempt plus exactly one retry), then falls back.
        Assert.Equal(new[] { "primary-model", "primary-model", "fallback-model" }, provider.CallsByModel);
    }

    /// <summary>
    /// Phase 10 hardening — a RateLimited (HTTP 429) failure honors the provider's own Retry-After
    /// value rather than the fixed exponential backoff used for ordinary transient failures.
    /// </summary>
    [Fact]
    public async Task ClassifyOneAsync_RateLimited_HonorsRetryAfter_ThenSucceeds()
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

        var callCount = 0;
        var provider = new FakeAiClassificationProvider
        {
            Behavior = (_, model, _, _) =>
            {
                callCount++;
                return Task.FromResult(callCount == 1
                    ? new ClassificationAttemptResult(false, "OpenRouter", model, null, "OpenRouter returned HTTP 429: rate limited.", 1,
                        ClassificationFailureCategory.RateLimited, TimeSpan.FromSeconds(7))
                    : new ClassificationAttemptResult(true, "OpenRouter", model,
                        new ClassificationResponse(true, "PRODUCT_INQUIRY", true, true, "HIGH", 0.9, "Pricing question."), null, 10));
            }
        };
        var delay = new NoOpRetryDelay();
        var service = new EmailClassificationService(db, provider, delay, new AiCircuitBreakerStore(TimeProvider.System), NullLogger<EmailClassificationService>.Instance);

        var decision = await service.ClassifyOneAsync(message.Id, CancellationToken.None);

        Assert.Equal(ImportanceDecision.Important, decision);
        Assert.Equal(2, provider.CallsByModel.Count);
        // The exact Retry-After value was honored, not the exponential-backoff formula.
        Assert.Equal(TimeSpan.FromSeconds(7), Assert.Single(delay.RequestedDelays));
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
        var service = new EmailClassificationService(db, provider, new NoOpRetryDelay(), new AiCircuitBreakerStore(TimeProvider.System), NullLogger<EmailClassificationService>.Instance);

        var decision = await service.ClassifyOneAsync(message.Id, CancellationToken.None);

        Assert.Equal(ImportanceDecision.Important, decision);
        Assert.Equal(new[] { "primary-model", "fallback-model" }, provider.CallsByModel);

        var classification = await db.EmailClassifications.SingleAsync(c => c.EmailMessageId == message.Id);
        Assert.Equal("fallback-model", classification.AiModel);
    }

    /// <summary>
    /// Phase 10 circuit breaker — checklist item 2/8 integration: once a model's circuit is OPEN,
    /// subsequent messages must skip it entirely (no HTTP call, no retry budget spent) and go
    /// straight to the fallback model. Uses a real (not shared-instance) AiCircuitBreakerStore
    /// across three sequential ClassifyOneAsync calls to prove the circuit persists across messages
    /// within one classification run, the way it would across a real batch.
    /// </summary>
    [Fact]
    public async Task ClassifyOneAsync_PrimaryModelCircuitOpensAfterRepeatedFailures_SubsequentMessagesSkipItEntirely()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        var profile = CreateSalesProfile();
        db.EmailAccounts.Add(account);
        db.ClassificationProfiles.Add(profile);
        db.AiModelConfigs.Add(CreateModel("primary-model", fallbackOrder: 0, maxRetries: 0));
        db.AiModelConfigs.Add(CreateModel("fallback-model", fallbackOrder: 1, maxRetries: 0));
        var messages = Enumerable.Range(1, 4)
            .Select(i => CreateMessage(account.Id, $"Price inquiry {i}", "Please send your latest price list."))
            .ToList();
        db.EmailMessages.AddRange(messages);
        await db.SaveChangesAsync();

        var provider = new FakeAiClassificationProvider
        {
            Behavior = (_, model, _, _) => model == "primary-model"
                ? Task.FromResult(new ClassificationAttemptResult(false, "OpenRouter", model, null, "primary down", 5, ClassificationFailureCategory.Transient))
                : Task.FromResult(new ClassificationAttemptResult(
                    true, "OpenRouter", model,
                    new ClassificationResponse(true, "PRICE_REQUEST", true, true, "HIGH", 0.9, "Pricing request."),
                    null, 12))
        };
        var circuitBreaker = new AiCircuitBreakerStore(TimeProvider.System);
        var service = new EmailClassificationService(db, provider, new NoOpRetryDelay(), circuitBreaker, NullLogger<EmailClassificationService>.Instance);

        // Messages 1-3: primary-model fails each time (3 consecutive counted failures -> opens).
        foreach (var message in messages.Take(3))
        {
            await service.ClassifyOneAsync(message.Id, CancellationToken.None);
        }
        Assert.Equal(CircuitState.Open, circuitBreaker.GetState("OpenRouter", "primary-model"));

        provider.CallsByModel.Clear();

        // Message 4: primary-model's circuit is OPEN, so it must be skipped entirely.
        var decision = await service.ClassifyOneAsync(messages[3].Id, CancellationToken.None);

        Assert.Equal(ImportanceDecision.Important, decision);
        Assert.DoesNotContain("primary-model", provider.CallsByModel);
        Assert.Equal(new[] { "fallback-model" }, provider.CallsByModel);
    }

    /// <summary>
    /// Phase 10 circuit breaker — checklist item 8: if every enabled model's circuit is open, no
    /// model is even attempted, and the message must reach the same safe ReviewRequired outcome as
    /// "every model was tried and failed" — never a silent/false success, per the explicit design
    /// decision recorded in the tracker.
    /// </summary>
    [Fact]
    public async Task ClassifyOneAsync_AllModelsCircuitOpen_ProducesReviewRequired_NoModelAttempted()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        var profile = CreateSalesProfile();
        db.EmailAccounts.Add(account);
        db.ClassificationProfiles.Add(profile);
        db.AiModelConfigs.Add(CreateModel("only-model", fallbackOrder: 0, maxRetries: 0));
        var messages = Enumerable.Range(1, 4)
            .Select(i => CreateMessage(account.Id, $"Price inquiry {i}", "Please send your latest price list."))
            .ToList();
        db.EmailMessages.AddRange(messages);
        await db.SaveChangesAsync();

        var provider = new FakeAiClassificationProvider
        {
            Behavior = (_, model, _, _) => Task.FromResult(new ClassificationAttemptResult(
                false, "OpenRouter", model, null, "down", 5, ClassificationFailureCategory.Transient))
        };
        var circuitBreaker = new AiCircuitBreakerStore(TimeProvider.System);
        var service = new EmailClassificationService(db, provider, new NoOpRetryDelay(), circuitBreaker, NullLogger<EmailClassificationService>.Instance);

        foreach (var message in messages.Take(3))
        {
            await service.ClassifyOneAsync(message.Id, CancellationToken.None);
        }
        Assert.Equal(CircuitState.Open, circuitBreaker.GetState("OpenRouter", "only-model"));

        provider.CallsByModel.Clear();
        var decision = await service.ClassifyOneAsync(messages[3].Id, CancellationToken.None);

        Assert.Equal(ImportanceDecision.ReviewRequired, decision);
        Assert.Empty(provider.CallsByModel); // no HTTP call was made — the circuit was open before any attempt.

        var reloaded = await db.EmailMessages.SingleAsync(m => m.Id == messages[3].Id);
        Assert.Equal(EmailProcessingStatus.ReviewRequired, reloaded.ProcessingStatus);
        Assert.Contains("circuit-open", reloaded.ProcessingError, StringComparison.OrdinalIgnoreCase);
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
        var service = new EmailClassificationService(db, provider, new NoOpRetryDelay(), new AiCircuitBreakerStore(TimeProvider.System), NullLogger<EmailClassificationService>.Instance);

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
        var service = new EmailClassificationService(db, provider, new NoOpRetryDelay(), new AiCircuitBreakerStore(TimeProvider.System), NullLogger<EmailClassificationService>.Instance);

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
        var service = new EmailClassificationService(db, provider, new NoOpRetryDelay(), new AiCircuitBreakerStore(TimeProvider.System), NullLogger<EmailClassificationService>.Instance);

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
        var service = new EmailClassificationService(db, provider, new NoOpRetryDelay(), new AiCircuitBreakerStore(TimeProvider.System), NullLogger<EmailClassificationService>.Instance);

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
        var service = new EmailClassificationService(db, provider, new NoOpRetryDelay(), new AiCircuitBreakerStore(TimeProvider.System), NullLogger<EmailClassificationService>.Instance);

        var result = await service.RunAsync(10, CancellationToken.None);

        Assert.Equal(2, result.ConsideredCount);
        Assert.Equal(1, result.ImportantCount);
        Assert.Equal(1, result.NotImportantCount);
        // Newsletter never reached the AI provider (deterministic exclude short-circuit).
        Assert.Single(provider.CallsByModel);
    }

    // --- Observability (Phase 10 hardening) — classification duration/result, model selection/retry logging ---

    [Fact]
    public async Task RunAsync_LogsBatchSummary_WithMessageCountAndOutcomeBreakdown()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        db.ClassificationProfiles.Add(CreateSalesProfile());
        db.AiModelConfigs.Add(CreateModel());
        db.EmailMessages.Add(CreateMessage(account.Id, "Price inquiry", "Please send your latest price list."));
        await db.SaveChangesAsync();

        var provider = new FakeAiClassificationProvider
        {
            Behavior = (_, model, _, _) => Task.FromResult(new ClassificationAttemptResult(
                true, "OpenRouter", model,
                new ClassificationResponse(true, "PRICE_REQUEST", true, true, "HIGH", 0.92, "Pricing request."),
                null, 10))
        };
        var logger = new CapturingLogger<EmailClassificationService>();
        var service = new EmailClassificationService(db, provider, new NoOpRetryDelay(), new AiCircuitBreakerStore(TimeProvider.System), logger);

        await service.RunAsync(10, CancellationToken.None);

        var summary = Assert.Single(logger.Entries, e => e.Message.Contains("batch processed"));
        Assert.Contains("1 important", summary.Message);
    }

    [Fact]
    public async Task ClassifyOneAsync_OnSuccess_LogsModelUsedAndAttemptCount()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        db.ClassificationProfiles.Add(CreateSalesProfile());
        db.AiModelConfigs.Add(CreateModel(maxRetries: 2));
        var message = CreateMessage(account.Id, "Price inquiry", "Please send your latest price list.");
        db.EmailMessages.Add(message);
        await db.SaveChangesAsync();

        var attempts = 0;
        var provider = new FakeAiClassificationProvider
        {
            Behavior = (_, model, _, _) =>
            {
                attempts++;
                if (attempts < 2)
                {
                    return Task.FromResult(new ClassificationAttemptResult(
                        false, "OpenRouter", model, null, "Transient failure", 5, ClassificationFailureCategory.Transient));
                }
                return Task.FromResult(new ClassificationAttemptResult(
                    true, "OpenRouter", model,
                    new ClassificationResponse(true, "PRICE_REQUEST", true, true, "HIGH", 0.92, "Pricing request."),
                    null, 10));
            }
        };
        var logger = new CapturingLogger<EmailClassificationService>();
        var service = new EmailClassificationService(db, provider, new NoOpRetryDelay(), new AiCircuitBreakerStore(TimeProvider.System), logger);

        await service.ClassifyOneAsync(message.Id, CancellationToken.None);

        var successLog = Assert.Single(logger.Entries, e => e.Message.Contains("classified successfully"));
        Assert.Contains("openai/gpt-4o-mini", successLog.Message);
        Assert.Contains("2 attempt", successLog.Message);
    }

    [Fact]
    public async Task ClassifyOneAsync_OnRateLimit_LogsRateLimitEventWithRetryAfter()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        db.ClassificationProfiles.Add(CreateSalesProfile());
        db.AiModelConfigs.Add(CreateModel(maxRetries: 1));
        var message = CreateMessage(account.Id, "Price inquiry", "Please send your latest price list.");
        db.EmailMessages.Add(message);
        await db.SaveChangesAsync();

        var provider = new FakeAiClassificationProvider
        {
            Behavior = (_, model, _, _) => Task.FromResult(new ClassificationAttemptResult(
                false, "OpenRouter", model, null, "Rate limited", 5,
                ClassificationFailureCategory.RateLimited, TimeSpan.FromSeconds(3)))
        };
        var logger = new CapturingLogger<EmailClassificationService>();
        var service = new EmailClassificationService(db, provider, new NoOpRetryDelay(), new AiCircuitBreakerStore(TimeProvider.System), logger);

        await service.ClassifyOneAsync(message.Id, CancellationToken.None);

        // Both attempts (initial + the one retry MaxRetries allows) hit the rate limit, so this
        // logs once per attempt — assert at least one fired, with the Retry-After value present.
        var rateLimitLogs = logger.Entries.Where(e => e.Message.Contains("rate-limited")).ToList();
        Assert.NotEmpty(rateLimitLogs);
        Assert.All(rateLimitLogs, log => Assert.Contains("00:00:03", log.Message));
    }
}
