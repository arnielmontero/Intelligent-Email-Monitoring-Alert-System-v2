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
    string ProfileExcludeDefinitions);

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
    string Summary);

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
    long DurationMs);

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
