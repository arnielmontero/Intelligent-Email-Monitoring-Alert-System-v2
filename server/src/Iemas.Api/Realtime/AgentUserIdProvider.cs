using Microsoft.AspNetCore.SignalR;

namespace Iemas.Api.Realtime;

/// <summary>
/// Requirements §69 (Agent identity is the server-issued Agent ID, never email/IP/client name
/// alone). SignalR's default <see cref="IUserIdProvider"/> uses <c>ClaimTypes.NameIdentifier</c>,
/// which this connection's JWT never sets (AgentHub's token carries "agent_id"/"employee_id"
/// instead — see AgentTokenService). This provider maps every AgentHub connection's SignalR "user"
/// to its Agent ID, which is what makes <c>Clients.User(agentId)</c> push targeting possible in
/// <see cref="AgentNotificationDispatcher"/>. One Agent = one SignalR user; if the same Agent has
/// more than one live connection (should not normally happen but is not assumed impossible),
/// SignalR already fans a User() call out to every connection under that user ID.
/// </summary>
public class AgentUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection)
    {
        return connection.User?.FindFirst("agent_id")?.Value;
    }
}
