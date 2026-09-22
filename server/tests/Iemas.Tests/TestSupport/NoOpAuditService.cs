using Iemas.Application.Common.Interfaces;

namespace Iemas.Tests.TestSupport;

public class NoOpAuditService : IAuditService
{
    public List<(string Action, string EntityType, string? EntityId, string? Details)> Entries { get; } = new();

    public Task LogAsync(string action, string entityType, string? entityId, string? details, CancellationToken cancellationToken = default)
    {
        Entries.Add((action, entityType, entityId, details));
        return Task.CompletedTask;
    }
}
