using Iemas.Application.Agents.Dtos;
using Iemas.Application.Common;
using Iemas.Application.Common.Interfaces;
using Iemas.Domain.Agents;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Agents;

/// <summary>§71 — CMS display fields for registered Agents.</summary>
public class AgentManagementService
{
    private readonly IAppDbContext _db;
    private readonly IAuditService _auditService;

    public AgentManagementService(IAppDbContext db, IAuditService auditService)
    {
        _db = db;
        _auditService = auditService;
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

    /// <summary>
    /// Permanently removes a Rejected or Revoked Agent with its technical log and credential. An
    /// approved Agent must be revoked first, and an Agent with Employee Activity (§67) is kept so
    /// that history is never erased.
    /// </summary>
    public async Task<Result<bool>> DeleteAsync(Guid agentId, CancellationToken cancellationToken)
    {
        var agent = await _db.Agents.Include(a => a.Credential).FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is null)
        {
            return Result<bool>.Failure("Agent registration not found.");
        }

        if (agent.RegistrationStatus is not (AgentRegistrationStatus.Rejected or AgentRegistrationStatus.Revoked))
        {
            return Result<bool>.Failure(agent.RegistrationStatus == AgentRegistrationStatus.Approved
                ? "Revoke this Agent before deleting it."
                : "Reject this registration request before deleting it.");
        }

        var activityCount = await _db.AgentCaseActions.CountAsync(a => a.AgentId == agentId, cancellationToken);
        if (activityCount > 0)
        {
            return Result<bool>.Failure(
                $"This Agent has {activityCount} Employee Activity record(s) and is kept so that history is not erased. It stays revoked and cannot reconnect.");
        }

        var logs = await _db.AgentLogs.Where(l => l.AgentId == agentId).ToListAsync(cancellationToken);
        _db.AgentLogs.RemoveRange(logs);
        if (agent.Credential is not null)
        {
            _db.AgentCredentials.Remove(agent.Credential);
        }
        _db.Agents.Remove(agent);
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("AGENT_DELETED", "Agent", agent.Id.ToString(),
            $"{agent.ClientName} ({agent.EnrollmentEmailAddress}), status {agent.RegistrationStatus}; {logs.Count} log line(s) removed",
            cancellationToken);
        return Result<bool>.Success(true);
    }
}
