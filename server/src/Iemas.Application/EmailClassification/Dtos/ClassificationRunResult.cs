namespace Iemas.Application.EmailClassification.Dtos;

public record ClassificationRunResult(
    int ConsideredCount,
    int ImportantCount,
    int NotImportantCount,
    int ReviewRequiredCount,
    int ProviderFailedCount,
    long DurationMs);
