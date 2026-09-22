using Iemas.Application.Agents.Dtos;
using Iemas.Application.Cases;
using Iemas.Application.Cases.Dtos;
using Iemas.Application.Common.Interfaces;
using Iemas.Domain.Agents;
using Iemas.Domain.Cases;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Agents;

/// <summary>
/// Requirements §75 (Agent Synchronization), §77 (Server is authoritative; Agent is cached
/// presentation and rebuilds entirely from a fresh sync), §9 (Action Required / Waiting UX split).
/// Reuses <see cref="CaseService"/>'s existing search rather than a parallel Case-query path —
/// the server has exactly one way to answer "what Cases does this Employee have," and this is it.
/// </summary>
public class AgentSyncService
{
    private readonly IAppDbContext _db;
    private readonly CaseService _caseService;

    public AgentSyncService(IAppDbContext db, CaseService caseService)
    {
        _db = db;
        _caseService = caseService;
    }

    public async Task<AgentSyncResponse?> SyncAsync(Guid agentId, Guid employeeId, CancellationToken cancellationToken)
    {
        var actionRequired = await _caseService.SearchAsync(
            new CaseListFilter(CaseWorkStatus.ActionRequired, employeeId, null, null, null), cancellationToken);

        // §9 "Waiting" bucket — Cases where the employee is not the one blocking progress.
        var waitingStatuses = new[] { CaseWorkStatus.WaitingForCustomer, CaseWorkStatus.WaitingForInternal, CaseWorkStatus.WaitingForApproval };
        var waiting = new List<CaseDto>();
        foreach (var status in waitingStatuses)
        {
            waiting.AddRange(await _caseService.SearchAsync(new CaseListFilter(status, employeeId, null, null, null), cancellationToken));
        }

        var agent = await _db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is null) return null;

        agent.LastConnectedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        _db.AgentLogs.Add(new AgentLog { AgentId = agentId, EventType = AgentLogEventType.Sync });
        await _db.SaveChangesAsync(cancellationToken);

        return new AgentSyncResponse(actionRequired, waiting.OrderByDescending(c => c.LastActivityAt).ToList(), DateTimeOffset.UtcNow);
    }

    /// <summary>§8.1 "Send heartbeat information," §71 Connection status. A heartbeat re-affirms Connected without a full sync payload.</summary>
    public async Task<bool> RecordHeartbeatAsync(Guid agentId, HeartbeatRequest request, CancellationToken cancellationToken)
    {
        var agent = await _db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is null) return false;

        agent.LastHeartbeatAt = DateTimeOffset.UtcNow;
        agent.ConnectionStatus = AgentConnectionStatus.Connected;
        if (!string.IsNullOrWhiteSpace(request.AgentVersion))
        {
            agent.AgentVersion = request.AgentVersion;
        }
        await _db.SaveChangesAsync(cancellationToken);

        _db.AgentLogs.Add(new AgentLog { AgentId = agentId, EventType = AgentLogEventType.Heartbeat, Detail = request.AgentVersion });
        await _db.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>Called on SignalR disconnect (see AgentHub) — §71 Connection status transitions to Disconnected without touching RegistrationStatus.</summary>
    public async Task RecordDisconnectAsync(Guid agentId, string? reason, CancellationToken cancellationToken)
    {
        var agent = await _db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is null) return;

        agent.ConnectionStatus = AgentConnectionStatus.Disconnected;
        await _db.SaveChangesAsync(cancellationToken);

        _db.AgentLogs.Add(new AgentLog { AgentId = agentId, EventType = AgentLogEventType.Disconnected, Detail = reason });
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>§8.1 "Report errors."</summary>
    public async Task RecordErrorAsync(Guid agentId, string detail, CancellationToken cancellationToken)
    {
        var agentExists = await _db.Agents.AnyAsync(a => a.Id == agentId, cancellationToken);
        if (!agentExists) return;

        _db.AgentLogs.Add(new AgentLog { AgentId = agentId, EventType = AgentLogEventType.Error, Detail = Truncate(detail, 4000) });
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
