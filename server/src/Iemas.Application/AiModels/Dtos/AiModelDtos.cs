namespace Iemas.Application.AiModels.Dtos;

public record AiModelDto(
    Guid Id,
    string Provider,
    string ModelIdentifier,
    string DisplayName,
    bool Enabled,
    bool IsDefault,
    string TaskCapability,
    int TimeoutSeconds,
    int MaxRetries,
    int FallbackOrder);

public record CreateAiModelRequest(
    string Provider,
    string ModelIdentifier,
    string DisplayName,
    bool Enabled,
    bool IsDefault,
    string TaskCapability,
    int TimeoutSeconds,
    int MaxRetries,
    int FallbackOrder);

public record UpdateAiModelRequest(
    string DisplayName,
    bool Enabled,
    bool IsDefault,
    string TaskCapability,
    int TimeoutSeconds,
    int MaxRetries,
    int FallbackOrder);

public record TestAiModelResult(bool Succeeded, string? ErrorMessage, long DurationMs);
