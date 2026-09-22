using Iemas.Domain.Common;
using Iemas.Domain.Email;

namespace Iemas.Domain.Ai;

/// <summary>
/// Requirements §25 — stores the AI result plus everything needed for later investigation
/// (§89: "why was this classified this way?"). One row per <see cref="EmailMessage"/> — a
/// re-classification (e.g. admin-triggered re-run) overwrites this row's AI fields but the
/// deterministic pre-filter fields and correction fields are handled separately (§30).
///
/// Deliberately keeps three distinct concepts separate per the Phase 4 boundary the build
/// instructions call out:
///   1. Deterministic pre-filter outcome (<see cref="DeterministicFilterMatched"/> and friends) —
///      keyword/profile include-exclude, computed without calling the AI.
///   2. Raw AI classification (<see cref="Relevance"/>, <see cref="Category"/>, <see cref="AiConfidence"/>, ...).
///   3. Final importance decision (<see cref="Decision"/>) — the deterministic confidence-policy
///      combination of 1 and 2. Core Principle §3.9: "AI recommends; deterministic business rules
///      control workflow."
/// </summary>
public class EmailClassification : Entity
{
    public Guid EmailMessageId { get; set; }
    public EmailMessage EmailMessage { get; set; } = null!;

    public Guid? ClassificationProfileId { get; set; }
    public ClassificationProfile? ClassificationProfile { get; set; }

    // --- Stage 0: deterministic subject/content filtering (runs before any AI call) ---

    /// <summary>True if the deterministic include/exclude filter found a positive signal worth sending to the AI.</summary>
    public bool DeterministicFilterMatched { get; set; }

    /// <summary>Which include/exclude term(s) drove the deterministic decision — for investigation (§89).</summary>
    public string? DeterministicFilterReason { get; set; }

    // --- Stage 1/2: raw AI classification result (§24, §25) ---

    public ClassificationRelevance? Relevance { get; set; }
    public string? Category { get; set; }
    public bool? ActionRequired { get; set; }
    public bool? ResponseExpected { get; set; }
    public ClassificationPriority? Priority { get; set; }
    public double? AiConfidence { get; set; }
    public string? Summary { get; set; }

    public string? AiProvider { get; set; }
    public string? AiModel { get; set; }
    public string? PromptProfileVersion { get; set; }
    public long? ProcessingDurationMs { get; set; }
    public string? ProcessingError { get; set; }

    // --- Final decision (§26, Core Principle 9) ---

    public ConfidenceBand? ConfidenceBand { get; set; }
    public ImportanceDecision Decision { get; set; } = ImportanceDecision.ReviewRequired;
    public string? DecisionReason { get; set; }

    // --- §30 False Classification Handling — admin corrections, audited separately via AuditLog ---

    public bool IsManuallyCorrected { get; set; }
    public ClassificationRelevance? CorrectedRelevance { get; set; }
    public string? CorrectedCategory { get; set; }
    public ClassificationPriority? CorrectedPriority { get; set; }

    public DateTimeOffset ClassifiedAt { get; set; } = DateTimeOffset.UtcNow;
}
