using Iemas.Api.Realtime;
using Iemas.Application.Agents;
using Iemas.Application.Cases;
using Iemas.Domain.Agents;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Tests.Agents;

/// <summary>§71 — an Agent that reconnects must stay Connected; only closing its last connection means offline.</summary>
public class AgentConnectionTests
{
    [Fact]
    public void Tracker_CountsOpenConnectionsPerAgent()
    {
        var tracker = new AgentConnectionTracker();
        var agent = Guid.NewGuid();
        tracker.Add(agent, "old");
        tracker.Add(agent, "new");

        Assert.Equal(1, tracker.Remove(agent, "old"));
        Assert.Equal(0, tracker.Remove(agent, "new"));
        Assert.Equal(0, tracker.Remove(Guid.NewGuid(), "unknown"));
    }

    [Fact]
    public async Task RecordDisconnect_WhileAnotherConnectionIsOpen_StaysConnected()
    {
        using var db = TestDbContext.CreateNew();
        var agent = new Agent
        {
            ClientName = "WEB-PC01", EnrollmentEmailAddress = "a@sawo.com", RegistrationStatus = AgentRegistrationStatus.Approved,
            ConnectionStatus = AgentConnectionStatus.Connected, RegistrationRequestToken = Guid.NewGuid().ToString(),
        };
        db.Agents.Add(agent);
        await db.SaveChangesAsync();
        var service = new AgentSyncService(db, new CaseService(db));

        await service.RecordDisconnectAsync(agent.Id, null, CancellationToken.None, otherConnectionsOpen: true);
        Assert.Equal(AgentConnectionStatus.Connected, (await db.Agents.AsNoTracking().SingleAsync()).ConnectionStatus);

        await service.RecordDisconnectAsync(agent.Id, null, CancellationToken.None);
        Assert.Equal(AgentConnectionStatus.Disconnected, (await db.Agents.AsNoTracking().SingleAsync()).ConnectionStatus);
    }
}
