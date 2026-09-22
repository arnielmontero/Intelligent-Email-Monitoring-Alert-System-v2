using Iemas.Application.Agents.Dtos;
using Iemas.Application.Common.Interfaces;
using Iemas.Domain.Agents;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Agents;

/// <summary>§71 — CMS display fields for registered Agents.</summary>
public class AgentManagementService
{
    private readonly IAppDbContext _db;

    public AgentManagementService(IAppDbContext db)
    {
        _db = db;
    }

    public async Task<List<AgentDto>> GetAllAsync(AgentRegistrationStatus? status, CancellationToken cancellationToken)
    {
        var query = _db.Agents.AsNoTracking().AsQueryable();
        if (status is not null) query = query.Where(a => a.RegistrationStatus == status);

        // Order before projecting — EF Core can't translate ORDER BY after a record-constructing
        // Select (see EmailAccountService for the original discovery of this issue).
        return await query
            .OrderByDescending(a => a.RegisteredAt)
            .Select(a => new AgentDto(
                a.Id, a.ClientName, a.EnrollmentEmailAddress, a.EmployeeId,
                a.Employee != null ? a.Employee.FullName : null,
                a.ServerAddress, a.LastKnownClientIp, a.AgentVersion,
                a.RegistrationStatus, a.ConnectionStatus, a.RegisteredAt,
                a.ApprovedByUserId, a.ApprovedByUser != null ? a.ApprovedByUser.Email : null, a.ApprovedAt,
                a.LastConnectedAt, a.LastHeartbeatAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<AgentDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        return await _db.Agents.AsNoTracking()
            .Where(a => a.Id == id)
            .Select(a => new AgentDto(
                a.Id, a.ClientName, a.EnrollmentEmailAddress, a.EmployeeId,
                a.Employee != null ? a.Employee.FullName : null,
                a.ServerAddress, a.LastKnownClientIp, a.AgentVersion,
                a.RegistrationStatus, a.ConnectionStatus, a.RegisteredAt,
                a.ApprovedByUserId, a.ApprovedByUser != null ? a.ApprovedByUser.Email : null, a.ApprovedAt,
                a.LastConnectedAt, a.LastHeartbeatAt))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>§67 Technical Agent Log, surfaced for investigation.</summary>
    public async Task<List<AgentLogDto>> GetLogsAsync(Guid agentId, CancellationToken cancellationToken)
    {
        return await _db.AgentLogs.AsNoTracking()
            .Where(l => l.AgentId == agentId)
            .OrderByDescending(l => l.OccurredAt)
            .Take(200)
            .Select(l => new AgentLogDto(l.Id, l.EventType, l.Detail, l.IpAddress, l.OccurredAt))
            .ToListAsync(cancellationToken);
    }
}
