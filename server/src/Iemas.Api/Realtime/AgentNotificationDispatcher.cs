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
    private readonly AgentConnectionTracker _connections;
    private readonly ILogger<AgentNotificationDispatcher> _logger;

    public AgentNotificationDispatcher(IHubContext<AgentHub> hubContext, IAppDbContext db, AgentConnectionTracker connections, ILogger<AgentNotificationDispatcher> logger)
    {
        _hubContext = hubContext;
        _db = db;
        _connections = connections;
        _logger = logger;
    }

    public async Task<int> NotifyEmployeeAsync(Guid employeeId, AgentPushCommand command, CancellationToken cancellationToken)
    {
        // §13 Option A (All Active Agents) — every approved Agent of this Employee with a live hub
        // connection receives the push. The live connection, not the stored ConnectionStatus, decides:
        // sign-in and REST heartbeats keep ConnectionStatus "Connected" even while the push channel is
        // down, and sending to a user with no open connection silently reaches nobody. An Agent without
        // a live connection is skipped; the notification stays Queued and is delivered when it
        // reconnects (NotificationService.DeliverMissedAsync).
        var agentIds = (await _db.Agents
            .Where(a => a.EmployeeId == employeeId && a.RegistrationStatus == AgentRegistrationStatus.Approved)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken))
            .Where(_connections.IsConnected)
            .ToList();

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
