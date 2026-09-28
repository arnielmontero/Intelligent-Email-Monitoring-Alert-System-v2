using Iemas.Application.Agents;
using Iemas.Domain.Agents;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Tests.Agents;

public class AgentManagementServiceTests
{
    private static Agent AddAgent(TestDbContext db, AgentRegistrationStatus status)
    {
        var agent = new Agent
        {
            ClientName = "WEB-PC01",
            EnrollmentEmailAddress = "user@sawo.com",
            RegistrationStatus = status,
            RegistrationRequestToken = Guid.NewGuid().ToString(),
        };
        db.Agents.Add(agent);
        db.AgentLogs.Add(new AgentLog { AgentId = agent.Id, EventType = AgentLogEventType.RegistrationRequested });
        return agent;
    }

    [Theory]
    [InlineData(AgentRegistrationStatus.Rejected)]
    [InlineData(AgentRegistrationStatus.Revoked)]
    public async Task DeleteAsync_RejectedOrRevoked_RemovesAgentLogsAndCredentialAndAudits(AgentRegistrationStatus status)
    {
        using var db = TestDbContext.CreateNew();
        var agent = AddAgent(db, status);
        db.AgentCredentials.Add(new AgentCredential { AgentId = agent.Id, KeyHash = "hash" });
        var other = AddAgent(db, AgentRegistrationStatus.Approved);
        await db.SaveChangesAsync();
        var audit = new NoOpAuditService();

        var result = await new AgentManagementService(db, audit).DeleteAsync(agent.Id, CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(other.Id, (await db.Agents.SingleAsync()).Id);
        Assert.Equal(other.Id, (await db.AgentLogs.SingleAsync()).AgentId);
        Assert.False(await db.AgentCredentials.AnyAsync());
        Assert.Equal("AGENT_DELETED", Assert.Single(audit.Entries).Action);
    }

    [Theory]
    [InlineData(AgentRegistrationStatus.Approved)]
    [InlineData(AgentRegistrationStatus.Pending)]
    public async Task DeleteAsync_ApprovedOrPending_IsRejected(AgentRegistrationStatus status)
    {
        using var db = TestDbContext.CreateNew();
        var agent = AddAgent(db, status);
        await db.SaveChangesAsync();

        var result = await new AgentManagementService(db, new NoOpAuditService()).DeleteAsync(agent.Id, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(await db.Agents.AnyAsync());
    }

    /// <summary>§67 — deleting an Agent must never erase what the employee did through it.</summary>
    [Fact]
    public async Task DeleteAsync_WithEmployeeActivity_IsRejected()
    {
        using var db = TestDbContext.CreateNew();
        var agent = AddAgent(db, AgentRegistrationStatus.Revoked);
        db.AgentCaseActions.Add(new AgentCaseAction
        {
            RequestId = "r1", AgentId = agent.Id, EmployeeId = Guid.NewGuid(), CaseId = Guid.NewGuid(),
            ActionType = CaseActionType.Acknowledged,
        });
        await db.SaveChangesAsync();

        var result = await new AgentManagementService(db, new NoOpAuditService()).DeleteAsync(agent.Id, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Employee Activity", result.Error);
        Assert.True(await db.Agents.AnyAsync());
        Assert.True(await db.AgentLogs.AnyAsync());
    }
}
