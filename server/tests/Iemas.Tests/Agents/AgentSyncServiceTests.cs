using Iemas.Application.Agents;
using Iemas.Application.Agents.Dtos;
using Iemas.Application.Cases;
using Iemas.Domain.Agents;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Iemas.Domain.Identity;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iemas.Tests.Agents;

public class AgentSyncServiceTests
{
    private static Employee CreateEmployee() => new() { FullName = "John Smith", Email = "john@sawo.com" };

    private static Agent CreateApprovedAgent(Guid employeeId)
    {
        return new Agent
        {
            ClientName = "Office PC",
            EnrollmentEmailAddress = "sales@sawo.com",
            EmployeeId = employeeId,
            RegistrationStatus = AgentRegistrationStatus.Approved,
            RegistrationRequestToken = Guid.NewGuid().ToString(),
        };
    }

    private static Case CreateCase(Guid emailAccountId, Guid ownerEmployeeId, CaseWorkStatus workStatus)
    {
        return new Case
        {
            CaseNumber = $"CASE-{Guid.NewGuid():N}"[..12],
            EmailAccountId = emailAccountId,
            CustomerEmailAddress = "customer@example.com",
            Subject = "Price Request",
            NormalizedSubject = "Price Request",
            OwnerEmployeeId = ownerEmployeeId,
            WorkStatus = workStatus,
            ReplyStatus = CaseReplyStatus.AwaitingReply,
            FirstEmailReceivedAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow,
        };
    }

    private static AgentSyncService CreateService(TestDbContext db) => new(db, new CaseService(db));

    /// <summary>§75 — sync returns Action Required and Waiting buckets, scoped to this Agent's own Employee.</summary>
    [Fact]
    public async Task SyncAsync_ReturnsActionRequiredAndWaitingCases_ScopedToEmployee()
    {
        using var db = TestDbContext.CreateNew();
        var employee = CreateEmployee();
        var otherEmployee = new Employee { FullName = "Other", Email = "other@sawo.com" };
        db.Employees.AddRange(employee, otherEmployee);
        var agent = CreateApprovedAgent(employee.Id);
        db.Agents.Add(agent);

        var account = new EmailAccount
        {
            EmailAddress = "sales@sawo.com", Purpose = EmailAccountPurpose.Inbound, Protocol = EmailProtocol.Imap,
            Host = "imap.example.com", Port = 993, Username = "sales@sawo.com", AuthMethod = EmailAuthMethod.Password,
        };
        db.EmailAccounts.Add(account);

        db.Cases.Add(CreateCase(account.Id, employee.Id, CaseWorkStatus.ActionRequired));
        db.Cases.Add(CreateCase(account.Id, employee.Id, CaseWorkStatus.WaitingForCustomer));
        db.Cases.Add(CreateCase(account.Id, otherEmployee.Id, CaseWorkStatus.ActionRequired)); // must not appear
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.SyncAsync(agent.Id, employee.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(result!.ActionRequired);
        Assert.Single(result.Waiting);
    }

    [Fact]
    public async Task SyncAsync_UnknownAgent_ReturnsNull()
    {
        using var db = TestDbContext.CreateNew();
        var service = CreateService(db);

        var result = await service.SyncAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task SyncAsync_UpdatesLastConnectedAt_RecordsSyncLog()
    {
        using var db = TestDbContext.CreateNew();
        var employee = CreateEmployee();
        db.Employees.Add(employee);
        var agent = CreateApprovedAgent(employee.Id);
        db.Agents.Add(agent);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.SyncAsync(agent.Id, employee.Id, CancellationToken.None);

        var reloaded = await db.Agents.SingleAsync();
        Assert.NotNull(reloaded.LastConnectedAt);

        var logs = await db.AgentLogs.Where(l => l.AgentId == agent.Id).ToListAsync();
        Assert.Contains(logs, l => l.EventType == AgentLogEventType.Sync);
    }

    /// <summary>§8.1/§71 — a heartbeat re-affirms Connected status and updates LastHeartbeatAt.</summary>
    [Fact]
    public async Task RecordHeartbeatAsync_UpdatesTimestampAndConnectionStatus()
    {
        using var db = TestDbContext.CreateNew();
        var employee = CreateEmployee();
        db.Employees.Add(employee);
        var agent = CreateApprovedAgent(employee.Id);
        agent.ConnectionStatus = AgentConnectionStatus.Disconnected;
        db.Agents.Add(agent);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var ok = await service.RecordHeartbeatAsync(agent.Id, new HeartbeatRequest("1.2.3"), CancellationToken.None);

        Assert.True(ok);
        var reloaded = await db.Agents.SingleAsync();
        Assert.NotNull(reloaded.LastHeartbeatAt);
        Assert.Equal(AgentConnectionStatus.Connected, reloaded.ConnectionStatus);
        Assert.Equal("1.2.3", reloaded.AgentVersion);
    }

    [Fact]
    public async Task RecordHeartbeatAsync_UnknownAgent_ReturnsFalse()
    {
        using var db = TestDbContext.CreateNew();
        var service = CreateService(db);

        var ok = await service.RecordHeartbeatAsync(Guid.NewGuid(), new HeartbeatRequest(null), CancellationToken.None);

        Assert.False(ok);
    }

    /// <summary>§71 — disconnect only changes ConnectionStatus, never RegistrationStatus (§74: offline is not itself a registration-affecting event).</summary>
    [Fact]
    public async Task RecordDisconnectAsync_SetsDisconnected_LeavesRegistrationStatusUntouched()
    {
        using var db = TestDbContext.CreateNew();
        var employee = CreateEmployee();
        db.Employees.Add(employee);
        var agent = CreateApprovedAgent(employee.Id);
        agent.ConnectionStatus = AgentConnectionStatus.Connected;
        db.Agents.Add(agent);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.RecordDisconnectAsync(agent.Id, "socket closed", CancellationToken.None);

        var reloaded = await db.Agents.SingleAsync();
        Assert.Equal(AgentConnectionStatus.Disconnected, reloaded.ConnectionStatus);
        Assert.Equal(AgentRegistrationStatus.Approved, reloaded.RegistrationStatus);

        var logs = await db.AgentLogs.Where(l => l.AgentId == agent.Id).ToListAsync();
        Assert.Contains(logs, l => l.EventType == AgentLogEventType.Disconnected);
    }

    /// <summary>§8.1 "Report errors" — recorded to the technical log, never silently dropped.</summary>
    [Fact]
    public async Task RecordErrorAsync_KnownAgent_RecordsErrorLog()
    {
        using var db = TestDbContext.CreateNew();
        var employee = CreateEmployee();
        db.Employees.Add(employee);
        var agent = CreateApprovedAgent(employee.Id);
        db.Agents.Add(agent);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.RecordErrorAsync(agent.Id, "Unhandled exception in tray icon renderer.", CancellationToken.None);

        var log = await db.AgentLogs.SingleAsync(l => l.AgentId == agent.Id);
        Assert.Equal(AgentLogEventType.Error, log.EventType);
        Assert.Contains("Unhandled exception", log.Detail);
    }
}
