using Iemas.Domain.Ai;

namespace Iemas.Application.EmailClassification;

public record DecisionPolicyResult(ConfidenceBand Band, ImportanceDecision Decision, string Reason);

/// <summary>
/// Requirements §26 (AI Confidence Policy) and Core Principle 9 ("AI recommends; deterministic
/// business rules control workflow"). This is the one place the raw AI relevance/confidence gets
/// turned into the final importance decision — kept separate from
/// <see cref="Iemas.Domain.Ai.EmailClassification"/> persistence and from the AI call itself so the
/// policy can be unit-tested without a provider and reused by both the live pipeline and the
/// §29 Test Classification endpoint.
/// </summary>
public static class ClassificationDecisionPolicy
{
    /// <summary>
    /// Assumption Log item #3 — HIGH >= 0.85, MEDIUM >= 0.5, LOW &lt; 0.5. Configurable per
    /// system settings/profile, not hardcoded into the calling code (only this default lives here).
    /// </summary>
    public const double DefaultHighThreshold = 0.85;
    public const double DefaultMediumThreshold = 0.5;

    /// <summary>Assumption Log item #4 — medium confidence defaults to ReviewRequired.</summary>
    public const bool DefaultTreatMediumAsReviewRequired = true;

    public static DecisionPolicyResult Decide(
        bool relevant,
        double confidence,
        double? highThreshold,
        double? mediumThreshold,
        bool? treatMediumAsReviewRequired,
        bool? legitimate = null,
        bool responseExpected = true,
        bool requireResponseForCase = false)
    {
        var high = highThreshold ?? DefaultHighThreshold;
        var medium = mediumThreshold ?? DefaultMediumThreshold;
        var mediumIsReview = treatMediumAsReviewRequired ?? DefaultTreatMediumAsReviewRequired;

        if (!relevant)
        {
            // §33 — non-relevant emails are still stored, just not turned into a Case. A confident
            // NOT_RELEVANT call is not sent to review; only an *uncertain* one is (below).
            return new DecisionPolicyResult(BandFor(confidence, high, medium), ImportanceDecision.NotImportant,
                $"AI classified as not relevant (confidence {confidence:P0}).");
        }

        var band = BandFor(confidence, high, medium);

        // An uncertain call goes to a person rather than being dropped on the AI's say-so.
        if (band == ConfidenceBand.Low)
        {
            return new DecisionPolicyResult(band, ImportanceDecision.ReviewRequired,
                $"Low confidence (confidence {confidence:P0} < {medium:P0}); needs a person to review it.");
        }

        if (legitimate == false)
        {
            return new DecisionPolicyResult(band, ImportanceDecision.NotImportant,
                "Not a legitimate business email per the Legitimate email rules (spam, marketing, automated or test message).");
        }

        if (requireResponseForCase && !responseExpected)
        {
            return new DecisionPolicyResult(band, ImportanceDecision.NotImportant,
                "Relevant, but no response is needed per the Needs-a-response rules; stored without creating a Case.");
        }

        return band switch
        {
            ConfidenceBand.High => new DecisionPolicyResult(band, ImportanceDecision.Important,
                $"High confidence relevant classification (confidence {confidence:P0} >= {high:P0})."),

            ConfidenceBand.Medium => mediumIsReview
                ? new DecisionPolicyResult(band, ImportanceDecision.ReviewRequired,
                    $"Medium confidence (confidence {confidence:P0}); configured to require review.")
                : new DecisionPolicyResult(band, ImportanceDecision.Important,
                    $"Medium confidence (confidence {confidence:P0}); configured to treat as important."),

            _ => new DecisionPolicyResult(band, ImportanceDecision.ReviewRequired,
                $"Low confidence (confidence {confidence:P0} < {medium:P0}); needs a person to review it."),
        };
    }

    private static ConfidenceBand BandFor(double confidence, double high, double medium)
    {
        if (confidence >= high) return ConfidenceBand.High;
        if (confidence >= medium) return ConfidenceBand.Medium;
        return ConfidenceBand.Low;
    }
}
