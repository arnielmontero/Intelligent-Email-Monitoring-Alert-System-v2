using Iemas.Application.Agents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Iemas.Api.Hubs;

/// <summary>
/// Requirements §76 (SignalR is a real-time transport, not a source of truth), §8.1 (maintain
/// SignalR connection, send heartbeat, synchronize after reconnect), §71 (connection status),
/// §72/§73 (only the predefined command/action vocabulary — enforced by the DTO-typed methods
/// below, there is no generic "invoke arbitrary method" surface).
///
/// This hub itself never decides Case/Notification state — every method here either reads
/// already-persisted state (via AgentSyncService, which itself only queries CaseService) or
/// records a technical connection event (AgentLog). No business decision is made inside a hub
/// method; that would risk exactly the "important state exists only inside SignalR memory"
/// problem §76 explicitly forbids.
/// </summary>
[Authorize(Policy = "RequireAgent")]
public class AgentHub : Hub
{
    private readonly AgentSyncService _syncService;
    private readonly Realtime.AgentConnectionTracker _connections;
    private readonly Application.Notifications.NotificationService _notifications;

    public AgentHub(AgentSyncService syncService, Realtime.AgentConnectionTracker connections, Application.Notifications.NotificationService notifications)
    {
        _syncService = syncService;
        _connections = connections;
        _notifications = notifications;
    }

    private Guid AgentId => Guid.Parse(Context.User!.FindFirst("agent_id")!.Value);
    private Guid EmployeeId => Guid.Parse(Context.User!.FindFirst("employee_id")!.Value);

    public override async Task OnConnectedAsync()
    {
        // §75 — SYNC on every connect/reconnect; the server remains authoritative and the Agent
        // rebuilds its UI entirely from what this call returns, never from anything cached client-side.
        _connections.Add(AgentId, Context.ConnectionId);
        await _syncService.RecordConnectedAsync(AgentId, Context.ConnectionAborted);
        await _syncService.SyncAsync(AgentId, EmployeeId, Context.ConnectionAborted);
        await base.OnConnectedAsync();

        // Pop-ups raised while this employee had no Agent connected are shown now instead of being lost.
        await _notifications.DeliverMissedAsync(EmployeeId, Context.ConnectionAborted);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var stillOpen = _connections.Remove(AgentId, Context.ConnectionId);
        await _syncService.RecordDisconnectAsync(AgentId, exception?.Message, CancellationToken.None, otherConnectionsOpen: stillOpen > 0);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>§73 PONG — response to a server PING (§72), proves liveness without a full sync round-trip.</summary>
    public Task Pong()
    {
        return Task.CompletedTask;
    }

    /// <summary>§8.1/§73 HEARTBEAT — periodic liveness signal.</summary>
    public async Task Heartbeat(string? agentVersion)
    {
        await _syncService.RecordHeartbeatAsync(AgentId, new Application.Agents.Dtos.HeartbeatRequest(agentVersion), Context.ConnectionAborted);
    }

    /// <summary>§73 SYNC — the Agent may request a fresh sync at any time (e.g. after the user manually refreshes), not only on connect.</summary>
    public async Task<Application.Agents.Dtos.AgentSyncResponse?> Sync()
    {
        return await _syncService.SyncAsync(AgentId, EmployeeId, Context.ConnectionAborted);
    }
}
