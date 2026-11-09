using Iemas.Domain.Cases;

namespace Iemas.Application.Common.Ai;

/// <summary>One message of an inquiry's conversation, as shown to the AI. Role is "Customer", "Agent" or "Internal".</summary>
public sealed record ConversationTurn(string Role, DateTimeOffset At, string Text);

public sealed record ResolutionAssessmentRequest(Guid CaseId, string Subject, IReadOnlyList<ConversationTurn> Turns);

public sealed record ResolutionAssessmentResult(
    bool Succeeded,
    ResolutionVerdict? Verdict,
    double Confidence,
    string? Reason,
    string Provider,
    string ModelIdentifier,
    string? Error,
    long DurationMs,
    AiTokenUsage? Usage = null);

/// <summary>
/// Phase 5 / Module 8 - the AI reads a whole inquiry conversation and judges whether the
/// customer's concern appears addressed. Advisory only: the result can at most mark an inquiry
/// "Potentially resolved"; only the agent can confirm it resolved (Q2). Never throws for provider
/// failures; reports them in the result.
/// </summary>
public interface IAiResolutionAssessor
{
    Task<ResolutionAssessmentResult> AssessAsync(ResolutionAssessmentRequest request, string modelIdentifier, TimeSpan timeout, CancellationToken cancellationToken);
}
