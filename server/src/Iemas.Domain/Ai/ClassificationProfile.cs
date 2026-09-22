using Iemas.Domain.Common;

namespace Iemas.Domain.Ai;

/// <summary>
/// Requirements §28 — Classification Profiles. CMS CRUD-managed, referenced by
/// <see cref="Email.EmailAccount"/> (currently by name — <see cref="Email.EmailAccount.ClassificationProfileName"/>
/// remains a plain string for backward compatibility with Phase 2/3 data; this entity is the
/// real target and new/updated accounts should resolve against <see cref="Name"/>).
///
/// Include/Exclude/Categories are stored as newline-separated plain text rather than a
/// normalized child table — matches the "predefined editable" spirit of §51 notification
/// templates and keeps CMS editing simple (a textarea) without overbuilding for V1.
/// </summary>
public class ClassificationProfile : Entity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Newline-separated category names this profile classifies into (§24 Stage 2).</summary>
    public string Categories { get; set; } = string.Empty;

    /// <summary>Newline-separated inclusion signals (§28 example: Product Inquiry, Price Request, ...).</summary>
    public string IncludeDefinitions { get; set; } = string.Empty;

    /// <summary>Newline-separated exclusion signals (§28 example: Advertisement, Newsletter, ...).</summary>
    public string ExcludeDefinitions { get; set; } = string.Empty;

    public string? ExampleSubject { get; set; }
    public string? ExampleContent { get; set; }
    public string? ExpectedClassification { get; set; }

    /// <summary>
    /// §26 — confidence thresholds must be configurable and must not be hardcoded into business
    /// logic. Null means "use the system-wide default" (see AiClassificationOptions); a profile
    /// may override per §28 "Confidence behavior".
    /// </summary>
    public double? HighConfidenceThreshold { get; set; }
    public double? MediumConfidenceThreshold { get; set; }

    /// <summary>
    /// §26 "MEDIUM confidence → Configurable behavior". When true, medium-confidence results are
    /// treated as ReviewRequired (assumption log default per tracker item #4); when false, medium
    /// is treated as Important. Null means "use the system-wide default".
    /// </summary>
    public bool? TreatMediumConfidenceAsReviewRequired { get; set; }
}
