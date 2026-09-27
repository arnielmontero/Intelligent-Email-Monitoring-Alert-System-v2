using Iemas.Api.Hubs;
using Iemas.Application.Common.Interfaces;
using Iemas.Domain.Agents;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iemas.Api.Realtime;

/// <summary>
/// Requirements §72/§76/§13 — see <see cref="IAgentNotificationDispatcher"/>. This is the one place
/// in the codebase that actually calls <see cref="IHubContext{AgentHub}"/> to push to a client;
/// callers (ReminderExecutionService, EscalationService, future Notification callers) never touch
/// SignalR directly, keeping "SignalR is a transport, not a source of truth" enforceable in one spot.
/// </summary>
public class AgentNotificationDispatcher : IAgentNotificationDispatcher
{
    private readonly IHubContext<AgentHub> _hubContext;
    private readonly IAppDbContext _db;
    private readonly ILogger<AgentNotificationDispatcher> _logger;

    public AgentNotificationDispatcher(IHubContext<AgentHub> hubContext, IAppDbContext db, ILogger<AgentNotificationDispatcher> logger)
    {
        _hubContext = hubContext;
        _db = db;
        _logger = logger;
    }

    public async Task<int> NotifyEmployeeAsync(Guid employeeId, AgentPushCommand command, CancellationToken cancellationToken)
    {
        // §13 Option A (All Active Agents) — every Agent belonging to this Employee that is
        // currently marked Connected receives the push. A Disconnected Agent is skipped here, not
        // queued: it will see current, authoritative state on its own next SYNC (§74/§75), which is
        // the whole reason server state, not SignalR delivery, remains the source of truth (§76).
        var agentIds = await _db.Agents
            .Where(a => a.EmployeeId == employeeId
                        && a.RegistrationStatus == AgentRegistrationStatus.Approved
                        && a.ConnectionStatus == AgentConnectionStatus.Connected)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);

        if (agentIds.Count == 0)
        {
            return 0;
        }

        var payload = new
        {
            type = command.Type.ToString().ToUpperInvariant() switch
            {
                "SHOWNOTIFICATION" => "SHOW_NOTIFICATION",
                "SHOWREMINDER" => "SHOW_REMINDER",
                "SHOWCASE" => "SHOW_CASE",
                "CANCELNOTIFICATION" => "CANCEL_NOTIFICATION",
                "SYNCREQUIRED" => "SYNC_REQUIRED",
                "PING" => "PING",
                _ => command.Type.ToString(),
            },
            caseId = command.CaseId,
            caseNumber = command.CaseNumber,
            title = command.Title,
            message = command.Message,
            serverTimeUtc = DateTimeOffset.UtcNow,
        };

        var delivered = 0;
        foreach (var agentId in agentIds)
        {
            try
            {
                // §69 — targets the SignalR "user" mapped to Agent ID by AgentUserIdProvider, never
                // by employee/email/IP. §8.2/§72 — "AgentCommand" is the one and only client method
                // name; the payload is always this closed, typed shape, never an arbitrary command.
                await _hubContext.Clients.User(agentId.ToString()).SendAsync("AgentCommand", payload, cancellationToken);
                delivered++;
            }
            catch (Exception ex)
            {
                // Best-effort by design (§76) — a push failure must never fault the caller's
                // already-committed business transaction. The Agent's next SYNC recovers state.
                _logger.LogWarning(ex, "Failed to push {CommandType} to Agent {AgentId}", command.Type, agentId);
            }
        }

        return delivered;
    }
}
