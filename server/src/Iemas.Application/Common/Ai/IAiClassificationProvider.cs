namespace Iemas.Application.Common.Ai;

/// <summary>Everything the AI needs to consider per §27 — subject, body, sender/recipient, thread context, profile.</summary>
public record ClassificationRequest(
    string Subject,
    string? BodyText,
    string FromAddress,
    string ToAddresses,
    bool IsPartOfExistingThread,
    string ProfileName,
    string ProfileCategories,
    string ProfileIncludeDefinitions,
    string ProfileExcludeDefinitions,
    Guid? EmailMessageId = null,
    string Purpose = AiUsagePurpose.EmailClassification,
    /// <summary>Admin-defined rules (System Configuration) for what counts as a legitimate business email.</summary>
    string? LegitimacyRules = null,
    /// <summary>Admin-defined rules (System Configuration) for when the sender expects a reply.</summary>
    string? ResponseRules = null);

public static class AiUsagePurpose
{
    public const string EmailClassification = "EmailClassification";
    public const string ModelTest = "ModelTest";
    public const string ProfileTest = "ProfileTest";
}

/// <summary>Token usage and cost for one provider call, as reported by the provider.</summary>
public record AiTokenUsage(int? PromptTokens, int? CompletionTokens, int? TotalTokens, decimal? CostUsd, string? GenerationId);

/// <summary>
/// Requirements §25 — the structured AI result. Confidence/relevance/category are the AI's raw
/// opinion; the workflow engine (not this type) applies confidence policy on top (§26, Core
/// Principle 9).
/// </summary>
public record ClassificationResponse(
    bool Relevant,
    string Category,
    bool ActionRequired,
    bool ResponseExpected,
    string Priority,
    double Confidence,
    string Summary,
    /// <summary>Genuine business email from a real sender (not spam, phishing, marketing or an automated notice). Null if the model did not say.</summary>
    bool? Legitimate = null);

/// <summary>
/// Phase 10 hardening — lets the provider (which alone knows whether a failure was an HTTP status,
/// a timeout, or a parse problem) tell the caller whether retrying is worth attempting, instead of
/// the caller string-matching <see cref="ClassificationAttemptResult.ErrorMessage"/>. A retry can
/// only help <see cref="Transient"/>; every other category means "the next attempt with the same
/// model would fail the same way," so retrying it only burns time and (for a rate limit) makes the
/// underlying problem worse.
/// </summary>
public enum ClassificationFailureCategory
{
    /// <summary>No failure — <see cref="ClassificationAttemptResult.Succeeded"/> is true.</summary>
    None = 0,

    /// <summary>Network/timeout/HTTP 5xx — worth an immediate retry.</summary>
    Transient = 1,

    /// <summary>HTTP 429 — worth retrying, but only after honoring the server's back-off signal.</summary>
    RateLimited = 2,

    /// <summary>Missing/invalid API key, HTTP 401/403 — retrying with the same key cannot succeed.</summary>
    AuthenticationFailure = 3,

    /// <summary>HTTP 400/404/422-class client error — the request itself is wrong; retrying it unchanged cannot succeed.</summary>
    InvalidRequest = 4,

    /// <summary>Response received but not parseable as the expected classification shape.</summary>
    MalformedResponse = 5,
}

/// <summary>
/// Requirements §82 — a specific AI model this call was attempted/answered with, so the caller can
/// record provider/model/duration regardless of success or failure (§25 "Store: ... Error if any").
/// </summary>
public record ClassificationAttemptResult(
    bool Succeeded,
    string Provider,
    string ModelIdentifier,
    ClassificationResponse? Response,
    string? ErrorMessage,
    long DurationMs,
    ClassificationFailureCategory FailureCategory = ClassificationFailureCategory.None,
    TimeSpan? RetryAfter = null,
    AiTokenUsage? Usage = null);

/// <summary>
/// Requirements §82 (AI Provider Management) — provider-specific integration logic (OpenRouter
/// today, potentially others later) must be isolated behind this abstraction, mirroring
/// <see cref="Iemas.Application.Common.Providers.IEmailProviderAdapter"/>'s role for mailboxes.
/// The classification workflow must never depend on OpenRouter/HTTP specifics directly.
/// </summary>
public interface IAiClassificationProvider
{
    /// <summary>
    /// Calls a single specific model. Never throws for provider/network/parse failures — those
    /// are reported via <see cref="ClassificationAttemptResult.Succeeded"/> so the caller can
    /// retry/fall back without exception-driven control flow (§83 AI Failure Handling).
    /// </summary>
    Task<ClassificationAttemptResult> ClassifyAsync(
        ClassificationRequest request,
        string modelIdentifier,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
