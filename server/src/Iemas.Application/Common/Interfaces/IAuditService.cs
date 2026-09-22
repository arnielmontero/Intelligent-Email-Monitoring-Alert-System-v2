namespace Iemas.Application.Common.Interfaces;

/// <summary>
/// Requirements §67, §84 — every security-sensitive/administrative change must be audited.
/// </summary>
public interface IAuditService
{
    Task LogAsync(string action, string entityType, string? entityId, string? details, CancellationToken cancellationToken = default);
}
