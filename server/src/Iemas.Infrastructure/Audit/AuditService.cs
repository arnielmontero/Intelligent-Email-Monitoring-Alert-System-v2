using Iemas.Application.Common.Interfaces;
using Iemas.Domain.Audit;

namespace Iemas.Infrastructure.Audit;

public class AuditService : IAuditService
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUserService _currentUser;

    public AuditService(IAppDbContext db, ICurrentUserService currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task LogAsync(string action, string entityType, string? entityId, string? details, CancellationToken cancellationToken = default)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = _currentUser.UserId,
            UserEmail = _currentUser.Email,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Details = details,
            IpAddress = _currentUser.IpAddress
        });

        await _db.SaveChangesAsync(cancellationToken);
    }
}
