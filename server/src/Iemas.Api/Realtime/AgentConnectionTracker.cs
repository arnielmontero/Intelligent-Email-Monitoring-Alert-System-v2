using System.Collections.Concurrent;

namespace Iemas.Api.Realtime;

/// <summary>
/// Open SignalR connections per Agent. An Agent reconnecting (e.g. after renewing its token) opens
/// the new connection before the old one closes; only the close of its last connection means it is
/// really offline. In-memory, so it assumes a single API instance.
/// </summary>
public class AgentConnectionTracker
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> _connections = new();

    public void Add(Guid agentId, string connectionId) =>
        _connections.GetOrAdd(agentId, _ => new ConcurrentDictionary<string, byte>())[connectionId] = 0;

    /// <summary>Returns how many connections the Agent still has open.</summary>
    public int Remove(Guid agentId, string connectionId)
    {
        if (!_connections.TryGetValue(agentId, out var set))
        {
            return 0;
        }
        set.TryRemove(connectionId, out _);
        return set.Count;
    }
}
