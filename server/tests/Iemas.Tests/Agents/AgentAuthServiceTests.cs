using Iemas.Application.Agents;
using Iemas.Application.Agents.Dtos;
using Iemas.Domain.Agents;
using Iemas.Domain.Identity;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iemas.Tests.Agents;

public class AgentAuthServiceTests
{
    private static Employee CreateEmployee() => new() { FullName = "John Smith", Email = "john@sawo.com", IsActive = true };

    /// <summary>Builds an Approved Agent with a known raw key, using AgentRegistrationService's own approval path (not a hand-built row) so the hash/encryption is realistic.</summary>
    private static async Task<(Guid AgentId, string RawKey)> CreateApprovedAgentAsync(TestDbContext db, FakeAgentTokenService tokenService, Employee employee)
    {
        db.Employees.Add(employee);
        db.EmailAccounts.Add(new Iemas.Domain.Email.EmailAccount
        {
            EmailAddress = "sales@sawo.com",
            Purpose = Iemas.Domain.Email.EmailAccountPurpose.Inbound,
            Protocol = Iemas.Domain.Email.EmailProtocol.Imap,
            Host = "imap.example.com",
            Port = 993,
            Username = "sales@sawo.com",
            AuthMethod = Iemas.Domain.Email.EmailAuthMethod.Password,
            OwnerEmployeeId = employee.Id,
        });
        await db.SaveChangesAsync();

        var registrationService = new AgentRegistrationService(db, tokenService, new PassThroughEncryptionService(), new NoOpAuditService());
        var registerResult = await registrationService.RegisterAsync(new RegisterAgentRequest("sales@sawo.com", "Office PC", null, null, null), null, CancellationToken.None);
        await registrationService.ApproveAsync(registerResult.Value!.AgentId, new ApproveAgentRequest(employee.Id), Guid.NewGuid(), CancellationToken.None);
        var statusResult = await registrationService.GetRegistrationStatusAsync(registerResult.Value.RegistrationRequestToken, CancellationToken.None);

        return (registerResult.Value.AgentId, statusResult.Value!.RegistrationKey!);
    }

    [Fact]
    public async Task AuthenticateAsync_ValidKey_Succeeds_TransitionsToConnected()
    {
        using var db = TestDbContext.CreateNew();
        var tokenService = new FakeAgentTokenService();
        var (agentId, rawKey) = await CreateApprovedAgentAsync(db, tokenService, CreateEmployee());
        var authService = new AgentAuthService(db, tokenService, new NoOpAuditService());

        var result = await authService.AuthenticateAsync(new AgentAuthenticateRequest(agentId, rawKey), "10.0.0.9", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotEmpty(result.Value!.AccessToken);
        var agent = await db.Agents.SingleAsync();
        Assert.Equal(AgentConnectionStatus.Connected, agent.ConnectionStatus);
        Assert.NotNull(agent.LastConnectedAt);
        Assert.Equal("10.0.0.9", agent.LastKnownClientIp);
    }

    /// <summary>§70 invariant check from the auth side: a key the client invented (never issued by the server) must never authenticate.</summary>
    [Fact]
    public async Task AuthenticateAsync_ClientInventedKey_NeverAuthenticates()
    {
        using var db = TestDbContext.CreateNew();
        var tokenService = new FakeAgentTokenService();
        var (agentId, _) = await CreateApprovedAgentAsync(db, tokenService, CreateEmployee());
        var authService = new AgentAuthService(db, tokenService, new NoOpAuditService());

        var result = await authService.AuthenticateAsync(new AgentAuthenticateRequest(agentId, "a-key-i-just-made-up-myself"), null, CancellationToken.None);

        Assert.False(result.Succeeded);
        var agent = await db.Agents.SingleAsync();
        Assert.NotEqual(AgentConnectionStatus.Connected, agent.ConnectionStatus);
    }

    [Fact]
    public async Task AuthenticateAsync_UnknownAgentId_Fails()
    {
        using var db = TestDbContext.CreateNew();
        var authService = new AgentAuthService(db, new FakeAgentTokenService(), new NoOpAuditService());

        var result = await authService.AuthenticateAsync(new AgentAuthenticateRequest(Guid.NewGuid(), "whatever"), null, CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task AuthenticateAsync_PendingAgent_CannotAuthenticate()
    {
        using var db = TestDbContext.CreateNew();
        db.EmailAccounts.Add(new Iemas.Domain.Email.EmailAccount
        {
            EmailAddress = "sales@sawo.com", Purpose = Iemas.Domain.Email.EmailAccountPurpose.Inbound,
            Protocol = Iemas.Domain.Email.EmailProtocol.Imap, Host = "imap.example.com", Port = 993,
            Username = "sales@sawo.com", AuthMethod = Iemas.Domain.Email.EmailAuthMethod.Password,
        });
        await db.SaveChangesAsync();
        var tokenService = new FakeAgentTokenService();
        var registrationService = new AgentRegistrationService(db, tokenService, new PassThroughEncryptionService(), new NoOpAuditService());
        var registerResult = await registrationService.RegisterAsync(new RegisterAgentRequest("sales@sawo.com", "Office PC", null, null, null), null, CancellationToken.None);

        var authService = new AgentAuthService(db, tokenService, new NoOpAuditService());
        var result = await authService.AuthenticateAsync(new AgentAuthenticateRequest(registerResult.Value!.AgentId, "anything"), null, CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    /// <summary>Revoked credential must never authenticate again, even with the originally-correct key.</summary>
    [Fact]
    public async Task AuthenticateAsync_RevokedCredential_Fails()
    {
        using var db = TestDbContext.CreateNew();
        var tokenService = new FakeAgentTokenService();
        var (agentId, rawKey) = await CreateApprovedAgentAsync(db, tokenService, CreateEmployee());
        var registrationService = new AgentRegistrationService(db, tokenService, new PassThroughEncryptionService(), new NoOpAuditService());
        await registrationService.RevokeAsync(agentId, new RevokeAgentRequest("compromised"), CancellationToken.None);

        var authService = new AgentAuthService(db, tokenService, new NoOpAuditService());
        var result = await authService.AuthenticateAsync(new AgentAuthenticateRequest(agentId, rawKey), null, CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    /// <summary>Expired credential must never authenticate, proven via a token service configured to issue already-expired keys.</summary>
    [Fact]
    public async Task AuthenticateAsync_ExpiredCredential_Fails()
    {
        using var db = TestDbContext.CreateNew();
        var tokenService = new FakeAgentTokenService { RegistrationKeyLifetime = TimeSpan.FromSeconds(-1) };
        var (agentId, rawKey) = await CreateApprovedAgentAsync(db, tokenService, CreateEmployee());

        var authService = new AgentAuthService(db, tokenService, new NoOpAuditService());
        var result = await authService.AuthenticateAsync(new AgentAuthenticateRequest(agentId, rawKey), null, CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    /// <summary>A replayed/leaked-but-since-rotated key (the old raw value after RotateCredentialAsync) must no longer work.</summary>
    [Fact]
    public async Task RotateCredentialAsync_InvalidatesOldKey_IssuesNewOne()
    {
        using var db = TestDbContext.CreateNew();
        var tokenService = new FakeAgentTokenService();
        var (agentId, oldRawKey) = await CreateApprovedAgentAsync(db, tokenService, CreateEmployee());
        var authService = new AgentAuthService(db, tokenService, new NoOpAuditService());

        var rotateResult = await authService.RotateCredentialAsync(agentId, CancellationToken.None);
        Assert.True(rotateResult.Succeeded);
        var newRawKey = rotateResult.Value!.RegistrationKey;
        Assert.NotEqual(oldRawKey, newRawKey);

        var oldKeyAttempt = await authService.AuthenticateAsync(new AgentAuthenticateRequest(agentId, oldRawKey), null, CancellationToken.None);
        Assert.False(oldKeyAttempt.Succeeded);

        var newKeyAttempt = await authService.AuthenticateAsync(new AgentAuthenticateRequest(agentId, newRawKey), null, CancellationToken.None);
        Assert.True(newKeyAttempt.Succeeded);
    }

    /// <summary>§67 — every failed authentication attempt is logged as a technical event, for investigation of repeated bad attempts.</summary>
    [Fact]
    public async Task AuthenticateAsync_Failure_RecordsAgentLog()
    {
        using var db = TestDbContext.CreateNew();
        var tokenService = new FakeAgentTokenService();
        var (agentId, _) = await CreateApprovedAgentAsync(db, tokenService, CreateEmployee());
        var authService = new AgentAuthService(db, tokenService, new NoOpAuditService());

        await authService.AuthenticateAsync(new AgentAuthenticateRequest(agentId, "wrong-key"), "10.0.0.99", CancellationToken.None);

        var logs = await db.AgentLogs.Where(l => l.AgentId == agentId).ToListAsync();
        Assert.Contains(logs, l => l.EventType == AgentLogEventType.AuthenticationFailed && l.IpAddress == "10.0.0.99");
    }
}
