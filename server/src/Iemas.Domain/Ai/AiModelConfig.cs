using Iemas.Domain.Common;

namespace Iemas.Domain.Ai;

/// <summary>
/// Requirements §82 — AI Provider Management. The model catalog must not be hardcoded; CMS
/// provides Add/View/Edit/Delete/Enable/Disable/Test/Default/Task routing/Fallback. V1 supports
/// exactly one <see cref="Provider"/> value (OpenRouter), but the entity itself is provider-agnostic
/// so a second provider can be added later without a schema change.
/// </summary>
public class AiModelConfig : Entity
{
    public string Provider { get; set; } = "OpenRouter";

    /// <summary>e.g. "openai/gpt-4o-mini" — the exact string sent to the provider API.</summary>
    public string ModelIdentifier { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    /// <summary>Only one row should be Default at a time; enforced in the service layer, not the DB (small table, admin-managed).</summary>
    public bool IsDefault { get; set; }

    /// <summary>e.g. "EmailClassification" — §82 "Task routing". Free text for V1 forward-compatibility with future AI tasks.</summary>
    public string TaskCapability { get; set; } = "EmailClassification";

    public int TimeoutSeconds { get; set; } = 30;
    public int MaxRetries { get; set; } = 2;

    /// <summary>Lower runs first. §82/§83 fallback order among enabled models sharing a TaskCapability.</summary>
    public int FallbackOrder { get; set; }

    /// <summary>Free-form JSON for provider-specific extras (temperature, etc.) — kept opaque here, parsed by the provider client only.</summary>
    public string? ConfigurationMetadata { get; set; }
}
