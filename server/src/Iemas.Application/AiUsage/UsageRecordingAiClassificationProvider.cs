using Iemas.Application.Common.Ai;
using Iemas.Domain.Ai;

namespace Iemas.Application.AiUsage;

/// <summary>Persists one usage record, independently of the caller's unit of work.</summary>
public interface IAiUsageRecorder
{
    Task RecordAsync(AiUsageRecord record, CancellationToken cancellationToken);
}

/// <summary>
/// Wraps the real AI provider so every call — from classification, model tests or profile
/// tests — is recorded for cost monitoring in one place. Recording is best-effort: it never
/// changes or fails the classification result.
/// </summary>
public class UsageRecordingAiClassificationProvider : IAiClassificationProvider
{
    private readonly IAiClassificationProvider _inner;
    private readonly IAiUsageRecorder _recorder;

    public UsageRecordingAiClassificationProvider(IAiClassificationProvider inner, IAiUsageRecorder recorder)
    {
        _inner = inner;
        _recorder = recorder;
    }

    public async Task<ClassificationAttemptResult> ClassifyAsync(
        ClassificationRequest request, string modelIdentifier, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await _inner.ClassifyAsync(request, modelIdentifier, timeout, cancellationToken);

        await _recorder.RecordAsync(new AiUsageRecord
        {
            Provider = result.Provider,
            ModelIdentifier = modelIdentifier,
            Purpose = request.Purpose,
            EmailMessageId = request.EmailMessageId,
            Succeeded = result.Succeeded,
            ErrorMessage = result.ErrorMessage is { Length: > 1000 } error ? error[..1000] : result.ErrorMessage,
            DurationMs = result.DurationMs,
            PromptTokens = result.Usage?.PromptTokens,
            CompletionTokens = result.Usage?.CompletionTokens,
            TotalTokens = result.Usage?.TotalTokens,
            CostUsd = result.Usage?.CostUsd,
            GenerationId = result.Usage?.GenerationId,
        }, CancellationToken.None);

        return result;
    }
}
