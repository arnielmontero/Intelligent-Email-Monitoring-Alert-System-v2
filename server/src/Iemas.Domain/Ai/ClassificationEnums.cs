namespace Iemas.Domain.Ai;

/// <summary>Requirements §24 Stage 1 — Relevance.</summary>
public enum ClassificationRelevance
{
    Relevant = 0,
    NotRelevant = 1,
    Uncertain = 2,
}

/// <summary>Requirements §25 — priority carried in the AI result.</summary>
public enum ClassificationPriority
{
    Low = 0,
    Medium = 1,
    High = 2,
}

/// <summary>
/// Requirements §26 — confidence policy. Deliberately separate from <see cref="ClassificationRelevance"/>:
/// relevance is the AI's semantic judgement, confidence band is the workflow engine's policy
/// decision about how much to trust that judgement (Core Principle §3.9 — "AI recommends;
/// deterministic business rules control workflow").
/// </summary>
public enum ConfidenceBand
{
    High = 0,
    Medium = 1,
    Low = 2,
}

/// <summary>
/// The final importance decision, distinct from raw AI relevance (§26, Core Principle 9). This is
/// what the deterministic workflow engine actually acts on — it folds in the deterministic
/// pre-filter outcome and the confidence policy, not just the AI's own opinion.
/// </summary>
public enum ImportanceDecision
{
    /// <summary>Confident important — proceeds to Case creation/matching (Phase 5).</summary>
    Important = 0,

    /// <summary>Confident not important — stored, no Case created, per §33 "non-relevant emails are still stored".</summary>
    NotImportant = 1,

    /// <summary>Low/medium confidence (per configured policy) or a fallback-still-failed AI call — §26, §83.</summary>
    ReviewRequired = 2,
}

/// <summary>
/// Requirements §67 pattern — a technical outcome log distinct from the admin-facing AuditLog,
/// matching <see cref="Iemas.Domain.Email.EmailIntakeOutcome"/>'s role for the intake pipeline.
/// </summary>
public enum AiClassificationOutcome
{
    Classified = 0,
    SkippedByDeterministicFilter = 1,
    ProviderFailed = 2,
    InvalidResponse = 3,
}
