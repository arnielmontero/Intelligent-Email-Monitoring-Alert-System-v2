using System.Diagnostics;
using Iemas.Application.Common.Ai;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.EmailClassification.Dtos;
using Iemas.Application.Operations;
using Iemas.Domain.Ai;
using Iemas.Domain.Email;
using Iemas.Domain.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iemas.Application.EmailClassification;

/// <summary>
/// Requirements §20 (pipeline: AI Classification stage, following intake's Persist stage),
/// §23-§27 (AI classification), §82/§83 (provider/model management and failure handling), §26
/// (confidence policy). Builds on Phase 3's intake pipeline rather than creating a parallel
/// email-processing path: this service only ever consumes
/// <see cref="EmailMessage.ProcessingStatus"/> == PendingClassification rows that
/// <c>EmailIntakeService</c> already produced — it does not fetch/persist email itself.
///
/// Boundary the build instructions call out explicitly: AI classification augments the
/// deterministic filtering rules, it does not replace them. <see cref="DeterministicEmailFilter"/>
/// always runs first; the AI call only happens when that stage defers to it. The AI's own output
/// never becomes the final decision by itself either — <see cref="ClassificationDecisionPolicy"/>
/// is the one deterministic place that turns AI relevance/confidence into
/// <see cref="ImportanceDecision"/> (Core Principle 9).
/// </summary>
public class EmailClassificationService
{
    private readonly IAppDbContext _db;
    private readonly IAiClassificationProvider _aiProvider;
    private readonly IRetryDelay _retryDelay;
    private readonly AiCircuitBreakerStore _circuitBreaker;
    private readonly ILogger<EmailClassificationService> _logger;

    public EmailClassificationService(
        IAppDbContext db, IAiClassificationProvider aiProvider, IRetryDelay retryDelay,
        AiCircuitBreakerStore circuitBreaker, ILogger<EmailClassificationService> logger)
    {
        _db = db;
        _aiProvider = aiProvider;
        _retryDelay = retryDelay;
        _circuitBreaker = circuitBreaker;
        _logger = logger;
    }

    public async Task<ClassificationRunResult> RunAsync(int batchSize, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // §91 Pause AI Classification — messages stay PendingClassification until resume.
        if (await _db.IsPausedAsync(PauseControl.AiClassification, cancellationToken))
        {
            _logger.LogInformation("Email classification skipped: AI Classification is paused (Emergency Pause)");
            return new ClassificationRunResult(0, 0, 0, 0, 0, stopwatch.ElapsedMilliseconds);
        }

        var messageIds = await _db.EmailMessages
            .Where(m => m.ProcessingStatus == EmailProcessingStatus.PendingClassification)
            .OrderBy(m => m.ReceivedAt)
            .Take(batchSize)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken);

        int important = 0, notImportant = 0, reviewRequired = 0, providerFailed = 0;

        foreach (var messageId in messageIds)
        {
            // Re-check current state before acting (§20 "Scheduled jobs must re-check current
            // state") — an admin could re-classify or the row could no longer be pending by the
            // time this iteration runs.
            var decision = await ClassifyOneAsync(messageId, cancellationToken);
            if (decision is null) continue;

            switch (decision)
            {
                case ImportanceDecision.Important: important++; break;
                case ImportanceDecision.NotImportant: notImportant++; break;
                case ImportanceDecision.ReviewRequired: reviewRequired++; break;
            }
        }

        stopwatch.Stop();
        _logger.LogInformation(
            "Email classification batch processed {MessageCount} message(s) in {ElapsedMilliseconds}ms: {Important} important, {NotImportant} not important, {ReviewRequired} review required, {ProviderFailed} provider failed",
            messageIds.Count, stopwatch.ElapsedMilliseconds, important, notImportant, reviewRequired, providerFailed);
        return new ClassificationRunResult(messageIds.Count, important, notImportant, reviewRequired, providerFailed, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Classifies a single message end to end. Returns null if the message was no longer
    /// eligible (already processed by a concurrent run/admin action) — not an error, just a
    /// quiet skip, matching <c>EmailIntakeService</c>'s pattern for stale-state re-checks.
    ///
    /// AI failures are non-fatal to intake by construction: this method never deletes or
    /// re-queues the message as a duplicate. On exhausted retries/fallback it marks the message
    /// ReviewRequired rather than leaving it stuck at PendingClassification forever or losing it.
    /// </summary>
    public async Task<ImportanceDecision?> ClassifyOneAsync(Guid emailMessageId, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        var message = await _db.EmailMessages.FirstOrDefaultAsync(m => m.Id == emailMessageId, cancellationToken);
        if (message is null || message.ProcessingStatus != EmailProcessingStatus.PendingClassification)
        {
            return null;
        }

        var account = await _db.EmailAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == message.EmailAccountId, cancellationToken);

        var profile = account?.ClassificationProfileName is { Length: > 0 } profileName
            ? await _db.ClassificationProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Name == profileName && p.Enabled, cancellationToken)
            : null;

        var filterResult = DeterministicEmailFilter.Evaluate(message.Subject, message.BodyText, profile);

        if (!filterResult.ShouldSendToAi)
        {
            return await PersistDeterministicOnlyAsync(message, profile, filterResult, stopwatch, cancellationToken);
        }

        var models = await _db.AiModelConfigs
            .AsNoTracking()
            .Where(m => m.Enabled && m.TaskCapability == "EmailClassification")
            .OrderBy(m => m.FallbackOrder)
            .ToListAsync(cancellationToken);

        if (models.Count == 0)
        {
            // §83 — no usable model configured is a provider-management problem, not a reason to
            // silently mark the email irrelevant. Goes to review, not lost.
            return await PersistFailureAsync(message, profile, filterResult, "No enabled AI model is configured for EmailClassification.", stopwatch, cancellationToken);
        }

        var rules = await EmailRules.LoadForPromptAsync(_db, cancellationToken);
        var request = new ClassificationRequest(
            message.Subject,
            message.BodyText,
            message.FromAddress,
            message.ToAddresses,
            message.InReplyTo is not null || message.ThreadId is not null,
            profile?.Name ?? "(none)",
            profile?.Categories ?? string.Empty,
            profile?.IncludeDefinitions ?? string.Empty,
            profile?.ExcludeDefinitions ?? string.Empty,
            message.Id,
            AiUsagePurpose.EmailClassification,
            rules.Legitimacy,
            rules.Response);

        ClassificationAttemptResult? lastAttempt = null;

        // §83 — retry, then fall back through the configured model order; only after every
        // enabled model has been exhausted does this become REVIEW_REQUIRED / PROCESSING_FAILED.
        //
        // Phase 10 hardening: not every failure is worth retrying with the same model. A missing
        // API key or an invalid request will fail identically on every attempt — retrying it only
        // delays reaching a model that might actually work (or REVIEW_REQUIRED). A malformed
        // response gets exactly one retry (it might be a one-off generation glitch, but a model
        // systematically returning bad JSON shouldn't burn its whole retry budget on repeats of
        // the same failure). A rate limit gets the provider's own Retry-After honored, capped so
        // one very long Retry-After can't stall the whole classification batch.
        //
        // Circuit breaker (per model, see the Build Progress Tracker design): TryAcquire/ReportOutcome
        // wrap the WHOLE retry loop for a model, not each individual attempt — the circuit only ever
        // sees one Success/CountedFailure/UncountedFailure per model per message, never one per
        // internal retry, so its 3-consecutive-failure threshold means 3 failed attempts across
        // messages, not 3 network blips within a single retry sequence.
        var anyModelAttempted = false;

        foreach (var model in models)
        {
            if (!_circuitBreaker.TryAcquire(model.Provider, model.ModelIdentifier, out var acquireTransitions))
            {
                // OPEN, or HALF-OPEN with another caller already holding the single probe slot —
                // skip this model entirely, consuming none of its retry budget, straight to fallback.
                continue;
            }
            LogCircuitTransitions(acquireTransitions);

            anyModelAttempted = true;

            var modelSucceeded = false;
            var attemptCount = 0;

            for (var attempt = 0; attempt <= model.MaxRetries; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attemptCount++;

                lastAttempt = await _aiProvider.ClassifyAsync(
                    request, model.ModelIdentifier, TimeSpan.FromSeconds(model.TimeoutSeconds), cancellationToken);

                if (lastAttempt.Succeeded)
                {
                    modelSucceeded = true;
                    break;
                }

                if (lastAttempt.FailureCategory == ClassificationFailureCategory.RateLimited)
                {
                    _logger.LogWarning(
                        "AI classification rate-limited by {Provider}/{Model} (attempt {Attempt}/{MaxAttempts}); Retry-After: {RetryAfter}",
                        model.Provider, model.ModelIdentifier, attemptCount, model.MaxRetries + 1, lastAttempt.RetryAfter);
                }

                if (!IsRetryable(lastAttempt.FailureCategory, attempt))
                {
                    break;
                }

                if (attempt < model.MaxRetries)
                {
                    await _retryDelay.WaitAsync(ComputeRetryDelay(lastAttempt, attempt), cancellationToken);
                }
            }

            var reportTransitions = _circuitBreaker.ReportOutcome(model.Provider, model.ModelIdentifier, ToCircuitOutcome(modelSucceeded, lastAttempt!.FailureCategory));
            LogCircuitTransitions(reportTransitions);

            if (modelSucceeded)
            {
                _logger.LogInformation(
                    "Email {EmailMessageId} classified successfully by {Provider}/{Model} after {AttemptCount} attempt(s){FallbackNote}",
                    message.Id, model.Provider, model.ModelIdentifier, attemptCount,
                    model.FallbackOrder > 0 ? " (used a fallback model, not the primary)" : "");
                return await PersistSuccessAsync(message, profile, filterResult, lastAttempt, stopwatch, cancellationToken);
            }

            _logger.LogWarning(
                "Email {EmailMessageId} classification failed on {Provider}/{Model} after {AttemptCount} attempt(s): {FailureCategory} — {ErrorMessage}",
                message.Id, model.Provider, model.ModelIdentifier, attemptCount, lastAttempt.FailureCategory, lastAttempt.ErrorMessage);
        }

        // Explicit design decision (see Build Progress Tracker): every enabled model's circuit
        // being open is its own named failure reason, distinguishable from "every model was tried
        // and failed" — no email is ever silently marked classified because nothing was attempted.
        var failureReason = !anyModelAttempted
            ? "All configured AI models are currently circuit-open (temporarily unavailable); no model was attempted."
            : lastAttempt?.ErrorMessage ?? "All configured AI models failed.";

        return await PersistFailureAsync(
            message, profile, filterResult,
            failureReason,
            stopwatch, cancellationToken, lastAttempt?.ModelIdentifier);
    }

    private async Task<ImportanceDecision> PersistDeterministicOnlyAsync(
        EmailMessage message, ClassificationProfile? profile, DeterministicFilterResult filterResult,
        Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        // The deterministic exclude match is treated as NotImportant directly — it is still a
        // deterministic *rule*, not the AI silently marking something irrelevant (§83's ban is on
        // the AI doing so; a configured exclude list is exactly the kind of explicit rule the
        // build instructions ask the AI to augment, not replace).
        var classification = await GetOrCreateClassificationAsync(message.Id, cancellationToken);
        classification.ClassificationProfileId = profile?.Id;
        classification.DeterministicFilterMatched = false;
        classification.DeterministicFilterReason = filterResult.Reason;
        classification.Decision = ImportanceDecision.NotImportant;
        classification.DecisionReason = $"Deterministic filter: {filterResult.Reason}";
        classification.ClassifiedAt = DateTimeOffset.UtcNow;

        message.ProcessingStatus = EmailProcessingStatus.Processed;
        message.Classification = "NOT_RELEVANT";
        message.ProcessingError = null;

        _db.AiClassificationLogs.Add(new AiClassificationLog
        {
            EmailMessageId = message.Id,
            Outcome = AiClassificationOutcome.SkippedByDeterministicFilter,
            Detail = filterResult.Reason,
            DurationMs = stopwatch.ElapsedMilliseconds,
        });

        await _db.SaveChangesAsync(cancellationToken);
        return ImportanceDecision.NotImportant;
    }

    private async Task<ImportanceDecision> PersistSuccessAsync(
        EmailMessage message, ClassificationProfile? profile, DeterministicFilterResult filterResult,
        ClassificationAttemptResult attempt, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var response = attempt.Response!;
        var policyResult = ClassificationDecisionPolicy.Decide(
            response.Relevant, response.Confidence,
            profile?.HighConfidenceThreshold, profile?.MediumConfidenceThreshold, profile?.TreatMediumConfidenceAsReviewRequired,
            response.Legitimate, response.ResponseExpected,
            await SystemSettingsService.GetBoolAsync(_db, SystemSettingKeys.RequireResponseForCase, cancellationToken));

        var classification = await GetOrCreateClassificationAsync(message.Id, cancellationToken);
        classification.ClassificationProfileId = profile?.Id;
        classification.DeterministicFilterMatched = true;
        classification.DeterministicFilterReason = filterResult.Reason;
        classification.Relevance = response.Relevant ? ClassificationRelevance.Relevant : ClassificationRelevance.NotRelevant;
        classification.Category = response.Category;
        classification.ActionRequired = response.ActionRequired;
        classification.ResponseExpected = response.ResponseExpected;
        classification.Legitimate = response.Legitimate;
        classification.Priority = Enum.TryParse<ClassificationPriority>(response.Priority, true, out var priority) ? priority : ClassificationPriority.Medium;
        classification.AiConfidence = response.Confidence;
        classification.Summary = response.Summary;
        classification.AiProvider = attempt.Provider;
        classification.AiModel = attempt.ModelIdentifier;
        classification.PromptProfileVersion = profile is null ? "default-v1" : $"{profile.Name}-v1";
        classification.ProcessingDurationMs = attempt.DurationMs;
        classification.ProcessingError = null;
        classification.ConfidenceBand = policyResult.Band;
        classification.Decision = policyResult.Decision;
        classification.DecisionReason = policyResult.Reason;
        classification.ClassifiedAt = DateTimeOffset.UtcNow;

        message.ProcessingStatus = policyResult.Decision == ImportanceDecision.ReviewRequired
            ? EmailProcessingStatus.ReviewRequired
            : EmailProcessingStatus.Processed;
        message.Classification = response.Category;
        message.AiConfidence = response.Confidence;
        message.AiModel = attempt.ModelIdentifier;
        message.ProcessingError = null;

        _db.AiClassificationLogs.Add(new AiClassificationLog
        {
            EmailMessageId = message.Id,
            Outcome = AiClassificationOutcome.Classified,
            Detail = policyResult.Reason,
            AiModel = attempt.ModelIdentifier,
            DurationMs = stopwatch.ElapsedMilliseconds,
        });

        await _db.SaveChangesAsync(cancellationToken);
        return policyResult.Decision;
    }

    private async Task<ImportanceDecision> PersistFailureAsync(
        EmailMessage message, ClassificationProfile? profile, DeterministicFilterResult filterResult,
        string error, Stopwatch stopwatch, CancellationToken cancellationToken, string? lastModelTried = null)
    {
        var classification = await GetOrCreateClassificationAsync(message.Id, cancellationToken);
        classification.ClassificationProfileId = profile?.Id;
        classification.DeterministicFilterMatched = true;
        classification.DeterministicFilterReason = filterResult.Reason;
        classification.ProcessingError = Truncate(error, 2000);
        classification.AiModel = lastModelTried;
        classification.Decision = ImportanceDecision.ReviewRequired;
        classification.DecisionReason = $"AI classification failed after retries/fallback: {Truncate(error, 500)}";
        classification.ClassifiedAt = DateTimeOffset.UtcNow;

        // §83 — "Do not silently mark an unclassified email as irrelevant." ReviewRequired keeps
        // the message visible/actionable rather than Processed (which would look like a completed,
        // trusted classification) or stuck forever at PendingClassification (which would make it
        // invisible to any "needs attention" queue).
        message.ProcessingStatus = EmailProcessingStatus.ReviewRequired;
        message.ProcessingError = Truncate(error, 2000);

        _db.AiClassificationLogs.Add(new AiClassificationLog
        {
            EmailMessageId = message.Id,
            Outcome = AiClassificationOutcome.ProviderFailed,
            Detail = Truncate(error, 2000),
            AiModel = lastModelTried,
            DurationMs = stopwatch.ElapsedMilliseconds,
        });

        await _db.SaveChangesAsync(cancellationToken);
        return ImportanceDecision.ReviewRequired;
    }

    private async Task<Iemas.Domain.Ai.EmailClassification> GetOrCreateClassificationAsync(Guid emailMessageId, CancellationToken cancellationToken)
    {
        var existing = await _db.EmailClassifications.FirstOrDefaultAsync(c => c.EmailMessageId == emailMessageId, cancellationToken);
        if (existing is not null) return existing;

        var created = new Iemas.Domain.Ai.EmailClassification { EmailMessageId = emailMessageId };
        _db.EmailClassifications.Add(created);
        return created;
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>
    /// Phase 10 hardening — only retry a failure the next attempt could plausibly fix.
    /// AuthenticationFailure/InvalidRequest will fail identically every time (wrong key, malformed
    /// request), so retrying them wastes the model's retry budget instead of moving on to fallback.
    /// MalformedResponse gets exactly one retry (attempt 0 only) per the explicit "limited retry,
    /// then fail safely" policy — a systematically-bad-JSON model shouldn't consume its whole
    /// retry budget repeating the same parse failure.
    /// </summary>
    private static bool IsRetryable(ClassificationFailureCategory category, int attemptIndex) => category switch
    {
        ClassificationFailureCategory.Transient or ClassificationFailureCategory.RateLimited => true,
        ClassificationFailureCategory.MalformedResponse => attemptIndex == 0,
        _ => false,
    };

    /// <summary>
    /// Circuit breaker design decision (see Build Progress Tracker) — AuthenticationFailure/
    /// InvalidRequest never affect circuit state: they are static configuration facts a cooldown
    /// cannot fix, not a "the model is temporarily unhealthy" signal. Every other failure category
    /// counts toward the opening threshold.
    /// </summary>
    private static CircuitOutcome ToCircuitOutcome(bool succeeded, ClassificationFailureCategory failureCategory)
    {
        if (succeeded) return CircuitOutcome.Success;
        return failureCategory is ClassificationFailureCategory.AuthenticationFailure or ClassificationFailureCategory.InvalidRequest
            ? CircuitOutcome.UncountedFailure
            : CircuitOutcome.CountedFailure;
    }

    /// <summary>
    /// Circuit breaker observability (design decision — see Build Progress Tracker): every state
    /// transition is logged, naming the model/provider, the transition, the consecutive-failure
    /// count, and the cooldown duration. Never logs the API key, prompt, email content, or raw AI
    /// response — matching the discipline already followed by OpenRouterClassificationProvider's
    /// own logging (Infrastructure layer), even though this call site is in Application.
    /// </summary>
    private void LogCircuitTransitions(IReadOnlyList<CircuitTransition> transitions)
    {
        foreach (var t in transitions)
        {
            _logger.LogWarning(
                "AI classification circuit breaker for {Provider}/{Model} transitioned {From} -> {To} (consecutive failures: {ConsecutiveFailures}, cooldown: {Cooldown})",
                t.Provider, t.ModelIdentifier, t.From, t.To, t.ConsecutiveFailures, t.CooldownDuration);
        }
    }

    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Phase 10 hardening — bounded exponential backoff (1s, 2s, 4s, ...) for ordinary transient
    /// failures, or the provider's own Retry-After for a rate limit (capped so one very long
    /// Retry-After can't stall the whole batch run past the next scheduled poll anyway).
    /// </summary>
    private static TimeSpan ComputeRetryDelay(ClassificationAttemptResult attempt, int attemptIndex)
    {
        if (attempt.FailureCategory == ClassificationFailureCategory.RateLimited && attempt.RetryAfter is TimeSpan retryAfter)
        {
            return retryAfter < TimeSpan.Zero ? TimeSpan.Zero : (retryAfter > MaxRetryDelay ? MaxRetryDelay : retryAfter);
        }

        var exponential = TimeSpan.FromSeconds(Math.Pow(2, attemptIndex));
        return exponential > MaxRetryDelay ? MaxRetryDelay : exponential;
    }
}
