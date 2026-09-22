using Iemas.Application.Common.Ai;

namespace Iemas.Tests.TestSupport;

/// <summary>
/// In-process fake used for EmailClassificationService unit tests, mirroring
/// FakeEmailProviderAdapter's role for EmailIntakeServiceTests. Live OpenRouter HTTP behavior is
/// not covered here — only IEMAS's own retry/fallback/decision-policy/persistence logic.
/// </summary>
public class FakeAiClassificationProvider : IAiClassificationProvider
{
    public Func<ClassificationRequest, string, TimeSpan, CancellationToken, Task<ClassificationAttemptResult>>? Behavior { get; set; }

    /// <summary>Records every (modelIdentifier) call made, in order — lets tests assert retry/fallback call counts.</summary>
    public List<string> CallsByModel { get; } = new();

    public Task<ClassificationAttemptResult> ClassifyAsync(ClassificationRequest request, string modelIdentifier, TimeSpan timeout, CancellationToken cancellationToken)
    {
        CallsByModel.Add(modelIdentifier);

        if (Behavior is not null)
        {
            return Behavior(request, modelIdentifier, timeout, cancellationToken);
        }

        return Task.FromResult(new ClassificationAttemptResult(
            true, "OpenRouter", modelIdentifier,
            new ClassificationResponse(true, "PRODUCT_INQUIRY", true, true, "HIGH", 0.95, "Customer asking about pricing."),
            null, 10));
    }
}
