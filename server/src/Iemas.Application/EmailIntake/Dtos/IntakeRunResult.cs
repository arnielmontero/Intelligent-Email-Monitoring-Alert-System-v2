namespace Iemas.Application.EmailIntake.Dtos;

public record IntakeRunResult(
    Guid EmailAccountId,
    bool Succeeded,
    int FetchedCount,
    int PersistedCount,
    int DuplicateCount,
    int MalformedCount,
    string? Error,
    long DurationMs);
