using Iemas.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Audit;

public record AuditLogDto(
    Guid Id,
    Guid? UserId,
    string? UserEmail,
    string Action,
    string EntityType,
    string? EntityId,
    string? Details,
    string? IpAddress,
    DateTimeOffset OccurredAt);

/// <summary>
/// Requirements §67 ("System Audit Log — what did an administrator/configuration change?"), §85
/// (Auditor/read-only role gets "Authorized history and audit access"), §84 ("Audit logging" as a
/// minimum security requirement), §86 (CMS nav: HISTORY & AUDIT → Audit Log). Read-only by design —
/// this is an append-only log (§67); nothing here ever writes or deletes a row. Every entry is
/// already written elsewhere (AgentRegistrationService, AgentAuthService, etc. via IAuditService),
/// this only exposes them for investigation.
/// </summary>
public class AuditQueryService
{
    private readonly IAppDbContext _db;

    public AuditQueryService(IAppDbContext db)
    {
        _db = db;
    }

    public async Task<List<AuditLogDto>> SearchAsync(string? action, string? entityType, DateTimeOffset? from, DateTimeOffset? to, int take, CancellationToken cancellationToken)
    {
        var query = _db.AuditLogs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(a => a.Action.Contains(action));
        }
        if (!string.IsNullOrWhiteSpace(entityType))
        {
            query = query.Where(a => a.EntityType == entityType);
        }
        if (from is DateTimeOffset fromValue)
        {
            query = query.Where(a => a.CreatedAt >= fromValue);
        }
        if (to is DateTimeOffset toValue)
        {
            query = query.Where(a => a.CreatedAt <= toValue);
        }

        return await query
            .OrderByDescending(a => a.CreatedAt)
            .Take(Math.Clamp(take, 1, 500))
            .Select(a => new AuditLogDto(a.Id, a.UserId, a.UserEmail, a.Action, a.EntityType, a.EntityId, a.Details, a.IpAddress, a.CreatedAt))
            .ToListAsync(cancellationToken);
    }
}
