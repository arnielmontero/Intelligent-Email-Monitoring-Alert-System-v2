using Iemas.Domain.Common;

namespace Iemas.Domain.Ai;

/// <summary>
/// One AI provider API call, for cost monitoring. Every attempt is recorded — including retries,
/// fallbacks and failures — because a failed call can still consume tokens. Never stores email
/// content.
/// </summary>
public class AiUsageRecord : Entity
{
    public string Provider { get; set; } = string.Empty;
    public string ModelIdentifier { get; set; } = string.Empty;

    /// <summary>EmailClassification, ModelTest or ProfileTest.</summary>
    public string Purpose { get; set; } = string.Empty;
    public Guid? EmailMessageId { get; set; }

    public bool Succeeded { get; set; }
    public string? ErrorMessage { get; set; }
    public long DurationMs { get; set; }

    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? TotalTokens { get; set; }

    /// <summary>USD as reported by the provider; null when the provider did not report a cost.</summary>
    public decimal? CostUsd { get; set; }
    public string? GenerationId { get; set; }
}
