namespace Iemas.Application.ClassificationProfiles.Dtos;

public record ClassificationProfileDto(
    Guid Id,
    string Name,
    string? Description,
    bool Enabled,
    string Categories,
    string IncludeDefinitions,
    string ExcludeDefinitions,
    string? ExampleSubject,
    string? ExampleContent,
    string? ExpectedClassification,
    double? HighConfidenceThreshold,
    double? MediumConfidenceThreshold,
    bool? TreatMediumConfidenceAsReviewRequired);

public record CreateClassificationProfileRequest(
    string Name,
    string? Description,
    bool Enabled,
    string Categories,
    string IncludeDefinitions,
    string ExcludeDefinitions,
    string? ExampleSubject,
    string? ExampleContent,
    string? ExpectedClassification,
    double? HighConfidenceThreshold,
    double? MediumConfidenceThreshold,
    bool? TreatMediumConfidenceAsReviewRequired);

public record UpdateClassificationProfileRequest(
    string Name,
    string? Description,
    bool Enabled,
    string Categories,
    string IncludeDefinitions,
    string ExcludeDefinitions,
    string? ExampleSubject,
    string? ExampleContent,
    string? ExpectedClassification,
    double? HighConfidenceThreshold,
    double? MediumConfidenceThreshold,
    bool? TreatMediumConfidenceAsReviewRequired);

/// <summary>§29 — Classification Testing. Never creates a real notification or Case (that dependency doesn't exist until Phase 5 anyway).</summary>
public record TestClassificationRequest(string Subject, string Content, Guid? ClassificationProfileId);

public record TestClassificationResult(
    bool Relevant,
    string Category,
    bool ActionRequired,
    string Priority,
    double Confidence,
    string Summary,
    string DeterministicFilterOutcome,
    string FinalDecision,
    string? Error,
    bool? Legitimate = null,
    bool? ResponseExpected = null,
    string? DecisionReason = null);
