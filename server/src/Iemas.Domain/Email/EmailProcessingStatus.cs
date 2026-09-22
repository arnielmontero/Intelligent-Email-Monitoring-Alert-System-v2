namespace Iemas.Domain.Email;

/// <summary>
/// Requirements §21 (Processing status), §20 (pipeline stages this phase is responsible for:
/// Fetch → Normalize → Duplicate Check → Persist). Classification/Case stages (Phase 4/5) are
/// out of scope here and are represented only as the "awaiting" starting point.
/// </summary>
public enum EmailProcessingStatus
{
    /// <summary>Persisted, not yet classified. Phase 4 (AI Classification) picks these up.</summary>
    PendingClassification = 0,

    /// <summary>Classification/case-matching pipeline (Phase 4/5) completed for this message.</summary>
    Processed = 1,

    /// <summary>Normalization or persistence failed in a way that needs investigation (§21 "Processing errors").</summary>
    Failed = 2,

    /// <summary>
    /// §26 — low/medium confidence classification (per configured policy) or an AI provider
    /// failure that exhausted retries/fallback (§83). Distinct from <see cref="Failed"/>: the
    /// message itself was fetched fine, it is the classification decision that needs a human.
    /// Non-fatal to intake — the message is never lost or reprocessed as a duplicate (§78).
    /// </summary>
    ReviewRequired = 3,
}
