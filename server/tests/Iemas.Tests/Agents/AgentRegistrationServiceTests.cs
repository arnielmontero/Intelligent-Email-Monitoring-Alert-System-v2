using Iemas.Application.Agents;
using Iemas.Application.Agents.Dtos;
using Iemas.Domain.Agents;
using Iemas.Domain.Email;
using Iemas.Domain.Identity;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iemas.Tests.Agents;

public class AgentRegistrationServiceTests
{
    private static Employee CreateEmployee(string email = "john@sawo.com") => new() { FullName = "John Smith", Email = email, IsActive = true };

    private static EmailAccount CreateInboundAccount(string email, Guid? ownerId = null)
    {
        return new EmailAccount
        {
            EmailAddress = email,
            Purpose = EmailAccountPurpose.Inbound,
            Protocol = EmailProtocol.Imap,
            Host = "imap.example.com",
            Port = 993,
            Username = email,
            AuthMethod = EmailAuthMethod.Password,
            IsActive = true,
            OwnerEmployeeId = ownerId,
        };
    }

    private static AgentRegistrationService CreateService(TestDbContext db, FakeAgentTokenService? tokenService = null, NoOpAuditService? auditService = null)
    {
        return new AgentRegistrationService(db, tokenService ?? new FakeAgentTokenService(), new PassThroughEncryptionService(), auditService ?? new NoOpAuditService());
    }

    /// <summary>§68 — registration requires the email to correspond to an enrolled inbound account.</summary>
    [Fact]
    public async Task RegisterAsync_EmailNotEnrolled_Fails()
    {
        using var db = TestDbContext.CreateNew();
        var service = CreateService(db);

        var result = await service.RegisterAsync(new RegisterAgentRequest("nobody@sawo.com", "Office PC", null, "1.0.0", null), "10.0.0.1", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(0, await db.Agents.CountAsync());
    }

    /// <summary>
    /// Regression test for the Phase 11 stored-XSS finding, applied here specifically because
    /// registration is unauthenticated — a malicious or compromised client could otherwise inject
    /// markup into ClientName with no bearer token required at all.
    /// </summary>
    [Fact]
    public async Task RegisterAsync_RejectsHtmlMarkupInClientName()
    {
        using var db = TestDbContext.CreateNew();
        db.EmailAccounts.Add(CreateInboundAccount("sales@sawo.com"));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var result = await service.RegisterAsync(
            new RegisterAgentRequest("sales@sawo.com", "<script>alert(1)</script>", null, "1.0.0", null),
            "10.0.0.1",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(0, await db.Agents.CountAsync());
    }

    [Fact]
    public async Task RegisterAsync_ValidRequest_CreatesPendingAgent_WithOpaqueToken()
    {
        using var db = TestDbContext.CreateNew();
        db.EmailAccounts.Add(CreateInboundAccount("sales@sawo.com"));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var result = await service.RegisterAsync(new RegisterAgentRequest("sales@sawo.com", "Office PC", "iemas.sawo.com", "1.0.0", "{\"os\":\"Windows 11\"}"), "10.0.0.1", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(AgentRegistrationStatus.Pending, result.Value!.Status);
        Assert.NotEmpty(result.Value.RegistrationRequestToken);

        var agent = await db.Agents.SingleAsync();
        Assert.Equal(AgentRegistrationStatus.Pending, agent.RegistrationStatus);
        Assert.Null(agent.Credential);
        Assert.Null(agent.EmployeeId);
    }

    /// <summary>The single most important invariant this phase specified: the client never generates/chooses the key — only Approve does, and only the server's token service produces it.</summary>
    [Fact]
    public async Task ApproveAsync_GeneratesKeyOnlyServerSide_NeverFromCallerInput()
    {
        using var db = TestDbContext.CreateNew();
        var employee = CreateEmployee();
        db.Employees.Add(employee);
        db.EmailAccounts.Add(CreateInboundAccount("sales@sawo.com", employee.Id));
        await db.SaveChangesAsync();

        var registrationService = CreateService(db);
        var registerResult = await registrationService.RegisterAsync(new RegisterAgentRequest("sales@sawo.com", "Office PC", null, null, null), null, CancellationToken.None);
        var agentId = registerResult.Value!.AgentId;
        var approverId = Guid.NewGuid();

        var approveResult = await registrationService.ApproveAsync(agentId, new ApproveAgentRequest(employee.Id), approverId, CancellationToken.None);

        Assert.True(approveResult.Succeeded);
        var agent = await db.Agents.Include(a => a.Credential).SingleAsync();
        Assert.Equal(AgentRegistrationStatus.Approved, agent.RegistrationStatus);
        Assert.Equal(employee.Id, agent.EmployeeId);
        Assert.Equal(approverId, agent.ApprovedByUserId);
        Assert.NotNull(agent.Credential);
        // The stored row never contains a plaintext key field the caller could have supplied —
        // only a hash and an encrypted transient blob, neither of which ApproveAgentRequest carries.
        Assert.NotEmpty(agent.Credential!.KeyHash);
        Assert.NotNull(agent.Credential.PendingKeyCiphertext);
    }

    [Fact]
    public async Task ApproveAsync_EmployeeDoesNotOwnEnrolledAccount_Fails()
    {
        using var db = TestDbContext.CreateNew();
        var employee = CreateEmployee();
        var otherEmployee = CreateEmployee("other@sawo.com");
        db.Employees.AddRange(employee, otherEmployee);
        // Account is owned by otherEmployee, but approval will be attempted for `employee`.
        db.EmailAccounts.Add(CreateInboundAccount("sales@sawo.com", otherEmployee.Id));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var registerResult = await service.RegisterAsync(new RegisterAgentRequest("sales@sawo.com", "Office PC", null, null, null), null, CancellationToken.None);

        var approveResult = await service.ApproveAsync(registerResult.Value!.AgentId, new ApproveAgentRequest(employee.Id), Guid.NewGuid(), CancellationToken.None);

        Assert.False(approveResult.Succeeded);
        var agent = await db.Agents.SingleAsync();
        Assert.Equal(AgentRegistrationStatus.Pending, agent.RegistrationStatus);
    }

    /// <summary>The other half of the invariant: the client automatically receives/stores the key via polling — never manual entry — and it is returned exactly once.</summary>
    [Fact]
    public async Task GetRegistrationStatusAsync_ReturnsRawKeyExactlyOnce_ThenNeverAgain()
    {
        using var db = TestDbContext.CreateNew();
        var employee = CreateEmployee();
        db.Employees.Add(employee);
        db.EmailAccounts.Add(CreateInboundAccount("sales@sawo.com", employee.Id));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var registerResult = await service.RegisterAsync(new RegisterAgentRequest("sales@sawo.com", "Office PC", null, null, null), null, CancellationToken.None);
        var token = registerResult.Value!.RegistrationRequestToken;

        await service.ApproveAsync(registerResult.Value.AgentId, new ApproveAgentRequest(employee.Id), Guid.NewGuid(), CancellationToken.None);

        var firstPoll = await service.GetRegistrationStatusAsync(token, CancellationToken.None);
        Assert.True(firstPoll.Succeeded);
        Assert.Equal(AgentRegistrationStatus.Approved, firstPoll.Value!.Status);
        Assert.NotNull(firstPoll.Value.RegistrationKey);
        var collectedKey = firstPoll.Value.RegistrationKey!;

        var secondPoll = await service.GetRegistrationStatusAsync(token, CancellationToken.None);
        Assert.True(secondPoll.Succeeded);
        Assert.Equal(AgentRegistrationStatus.Approved, secondPoll.Value!.Status);
        // Collected once already — the key must never be served again, even to the legitimate Agent re-polling.
        Assert.Null(secondPoll.Value.RegistrationKey);

        // The encrypted transient blob is cleared once collected — nothing recoverable remains.
        var credential = await db.AgentCredentials.SingleAsync();
        Assert.True(credential.RawKeyCollected);
        Assert.Null(credential.PendingKeyCiphertext);
        Assert.NotEmpty(collectedKey);
    }

    [Fact]
    public async Task GetRegistrationStatusAsync_StillPending_ReturnsNoKey()
    {
        using var db = TestDbContext.CreateNew();
        db.EmailAccounts.Add(CreateInboundAccount("sales@sawo.com"));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var registerResult = await service.RegisterAsync(new RegisterAgentRequest("sales@sawo.com", "Office PC", null, null, null), null, CancellationToken.None);
        var statusResult = await service.GetRegistrationStatusAsync(registerResult.Value!.RegistrationRequestToken, CancellationToken.None);

        Assert.True(statusResult.Succeeded);
        Assert.Equal(AgentRegistrationStatus.Pending, statusResult.Value!.Status);
        Assert.Null(statusResult.Value.RegistrationKey);
    }

    [Fact]
    public async Task GetRegistrationStatusAsync_UnknownToken_Fails()
    {
        using var db = TestDbContext.CreateNew();
        var service = CreateService(db);

        var result = await service.GetRegistrationStatusAsync("not-a-real-token", CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task RejectAsync_PendingAgent_MarksRejected_RecordsReason()
    {
        using var db = TestDbContext.CreateNew();
        db.EmailAccounts.Add(CreateInboundAccount("sales@sawo.com"));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var registerResult = await service.RegisterAsync(new RegisterAgentRequest("sales@sawo.com", "Office PC", null, null, null), null, CancellationToken.None);
        var result = await service.RejectAsync(registerResult.Value!.AgentId, new RejectAgentRequest("Not a recognized device."), CancellationToken.None);

        Assert.True(result.Succeeded);
        var agent = await db.Agents.SingleAsync();
        Assert.Equal(AgentRegistrationStatus.Rejected, agent.RegistrationStatus);
        Assert.Equal("Not a recognized device.", agent.RejectionReason);
    }

    [Fact]
    public async Task RejectAsync_AlreadyApproved_Fails()
    {
        using var db = TestDbContext.CreateNew();
        var employee = CreateEmployee();
        db.Employees.Add(employee);
        db.EmailAccounts.Add(CreateInboundAccount("sales@sawo.com", employee.Id));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var registerResult = await service.RegisterAsync(new RegisterAgentRequest("sales@sawo.com", "Office PC", null, null, null), null, CancellationToken.None);
        await service.ApproveAsync(registerResult.Value!.AgentId, new ApproveAgentRequest(employee.Id), Guid.NewGuid(), CancellationToken.None);

        var result = await service.RejectAsync(registerResult.Value.AgentId, new RejectAgentRequest("too late"), CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    /// <summary>§70 "Revocable. Invalid after revocation." — revoking also clears any still-pending encrypted key so nothing recoverable survives.</summary>
    [Fact]
    public async Task RevokeAsync_ApprovedAgent_RevokesCredential_ClearsAnyPendingKey()
    {
        using var db = TestDbContext.CreateNew();
        var employee = CreateEmployee();
        db.Employees.Add(employee);
        db.EmailAccounts.Add(CreateInboundAccount("sales@sawo.com", employee.Id));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var registerResult = await service.RegisterAsync(new RegisterAgentRequest("sales@sawo.com", "Office PC", null, null, null), null, CancellationToken.None);
        await service.ApproveAsync(registerResult.Value!.AgentId, new ApproveAgentRequest(employee.Id), Guid.NewGuid(), CancellationToken.None);

        var result = await service.RevokeAsync(registerResult.Value.AgentId, new RevokeAgentRequest("Device lost."), CancellationToken.None);

        Assert.True(result.Succeeded);
        var agent = await db.Agents.Include(a => a.Credential).SingleAsync();
        Assert.Equal(AgentRegistrationStatus.Revoked, agent.RegistrationStatus);
        Assert.NotNull(agent.Credential!.RevokedAt);
        Assert.False(agent.Credential.IsActive);
        Assert.Null(agent.Credential.PendingKeyCiphertext);
    }

    [Fact]
    public async Task RevokeAsync_AlreadyRevoked_Fails()
    {
        using var db = TestDbContext.CreateNew();
        db.EmailAccounts.Add(CreateInboundAccount("sales@sawo.com"));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var registerResult = await service.RegisterAsync(new RegisterAgentRequest("sales@sawo.com", "Office PC", null, null, null), null, CancellationToken.None);
        await service.RevokeAsync(registerResult.Value!.AgentId, new RevokeAgentRequest("first"), CancellationToken.None);

        var result = await service.RevokeAsync(registerResult.Value.AgentId, new RevokeAgentRequest("second"), CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    /// <summary>§67 Technical Agent Log — registration/approval/rejection/revocation each leave a distinct trace.</summary>
    [Fact]
    public async Task FullLifecycle_ProducesExpectedTechnicalLogTrail()
    {
        using var db = TestDbContext.CreateNew();
        var employee = CreateEmployee();
        db.Employees.Add(employee);
        db.EmailAccounts.Add(CreateInboundAccount("sales@sawo.com", employee.Id));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var registerResult = await service.RegisterAsync(new RegisterAgentRequest("sales@sawo.com", "Office PC", null, null, null), "10.0.0.5", CancellationToken.None);
        await service.ApproveAsync(registerResult.Value!.AgentId, new ApproveAgentRequest(employee.Id), Guid.NewGuid(), CancellationToken.None);
        await service.GetRegistrationStatusAsync(registerResult.Value.RegistrationRequestToken, CancellationToken.None);

        var logs = await db.AgentLogs.Where(l => l.AgentId == registerResult.Value.AgentId).ToListAsync();
        Assert.Contains(logs, l => l.EventType == AgentLogEventType.RegistrationRequested);
        Assert.Contains(logs, l => l.EventType == AgentLogEventType.Approved);
        Assert.Contains(logs, l => l.EventType == AgentLogEventType.Sync); // key-collection event
    }

    /// <summary>Admin actions are audited via the shared IAuditService, same as every other admin-facing change in the codebase.</summary>
    [Fact]
    public async Task ApproveAsync_WritesToAuditLog()
    {
        using var db = TestDbContext.CreateNew();
        var employee = CreateEmployee();
        db.Employees.Add(employee);
        db.EmailAccounts.Add(CreateInboundAccount("sales@sawo.com", employee.Id));
        await db.SaveChangesAsync();
        var audit = new NoOpAuditService();
        var service = CreateService(db, auditService: audit);

        var registerResult = await service.RegisterAsync(new RegisterAgentRequest("sales@sawo.com", "Office PC", null, null, null), null, CancellationToken.None);
        await service.ApproveAsync(registerResult.Value!.AgentId, new ApproveAgentRequest(employee.Id), Guid.NewGuid(), CancellationToken.None);

        Assert.Contains(audit.Entries, e => e.Action == "AGENT_APPROVED");
    }
}
