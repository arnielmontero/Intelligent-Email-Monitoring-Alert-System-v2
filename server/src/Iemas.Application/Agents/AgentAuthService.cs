using System.Security.Cryptography;
using Iemas.Application.Agents.Dtos;
using Iemas.Application.Common;
using Iemas.Application.Common.Interfaces;
using Iemas.Domain.Agents;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Agents;

/// <summary>
/// Requirements §68 "Agent Authenticates → CONNECTED", §69 (Agent Identity), §70 (rotation),
/// §71 (Connection status transitions). Verifies a Registration Key against its stored hash only
/// — the raw value collected once via <see cref="AgentRegistrationService.GetRegistrationStatusAsync"/>
/// is never persisted, so this service can only ever compare hashes, never recover a key.
/// </summary>
public class AgentAuthService
{
    private readonly IAppDbContext _db;
    private readonly IAgentTokenService _agentTokenService;
    private readonly IAuditService _auditService;

    public AgentAuthService(IAppDbContext db, IAgentTokenService agentTokenService, IAuditService auditService)
    {
        _db = db;
        _agentTokenService = agentTokenService;
        _auditService = auditService;
    }

    public async Task<Result<AgentAuthenticateResponse>> AuthenticateAsync(AgentAuthenticateRequest request, string? clientIp, CancellationToken cancellationToken)
    {
        var agent = await _db.Agents
            .Include(a => a.Credential)
            .Include(a => a.Employee)
            .FirstOrDefaultAsync(a => a.Id == request.AgentId, cancellationToken);

        if (agent is null)
        {
            // No row to attach an AgentLog to (FK requires a real Agent) — an unknown Agent ID is
            // not itself a meaningful technical event for a specific Agent's log.
            return Result<AgentAuthenticateResponse>.Failure("Invalid Agent ID or Registration Key.");
        }

        if (agent.Employee is null)
        {
            await LogFailureAsync(agent.Id, "Agent is not linked to an employee.", clientIp, cancellationToken);
            return Result<AgentAuthenticateResponse>.Failure("Invalid Agent ID or Registration Key.");
        }

        // §71 — only Approved (and not since revoked) agents may authenticate. A rejected or
        // revoked Agent presenting a stale/leaked key must be rejected exactly like an invalid one.
        if (agent.RegistrationStatus != AgentRegistrationStatus.Approved || agent.Credential is null)
        {
            await LogFailureAsync(agent.Id, $"Registration status is {agent.RegistrationStatus}, not Approved.", clientIp, cancellationToken);
            return Result<AgentAuthenticateResponse>.Failure("Invalid Agent ID or Registration Key.");
        }

        if (!agent.Credential.IsActive)
        {
            await LogFailureAsync(agent.Id, agent.Credential.RevokedAt is not null ? "Credential has been revoked." : "Credential has expired.", clientIp, cancellationToken);
            return Result<AgentAuthenticateResponse>.Failure("Invalid Agent ID or Registration Key.");
        }

        if (!FixedTimeEquals(HashKey(request.RegistrationKey), agent.Credential.KeyHash))
        {
            await LogFailureAsync(agent.Id, "Registration Key did not match.", clientIp, cancellationToken);
            return Result<AgentAuthenticateResponse>.Failure("Invalid Agent ID or Registration Key.");
        }

        agent.ConnectionStatus = AgentConnectionStatus.Connected;
        agent.LastConnectedAt = DateTimeOffset.UtcNow;
        agent.LastKnownClientIp = clientIp;
        await _db.SaveChangesAsync(cancellationToken);

        _db.AgentLogs.Add(new AgentLog { AgentId = agent.Id, EventType = AgentLogEventType.AuthenticationSucceeded, IpAddress = clientIp });
        _db.AgentLogs.Add(new AgentLog { AgentId = agent.Id, EventType = AgentLogEventType.Connected, IpAddress = clientIp });
        await _db.SaveChangesAsync(cancellationToken);

        var accessToken = _agentTokenService.GenerateAccessToken(agent);
        return Result<AgentAuthenticateResponse>.Success(new AgentAuthenticateResponse(
            accessToken, DateTimeOffset.UtcNow.AddMinutes(15), agent.Id, agent.EmployeeId!.Value, agent.Employee.FullName));
    }

    /// <summary>§70 rotation — the caller must already be an authenticated Agent (its current token proves possession of the still-active credential) to receive a freshly rotated key; the old hash is invalidated in the same transaction.</summary>
    public async Task<Result<RotateAgentCredentialResponse>> RotateCredentialAsync(Guid agentId, CancellationToken cancellationToken)
    {
        var agent = await _db.Agents.Include(a => a.Credential).FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is null || agent.Credential is null || agent.RegistrationStatus != AgentRegistrationStatus.Approved)
        {
            return Result<RotateAgentCredentialResponse>.Failure("Agent is not in a state that allows credential rotation.");
        }

        var (rawKey, expiresAt) = _agentTokenService.GenerateRegistrationKey();
        agent.Credential.KeyHash = HashKey(rawKey);
        agent.Credential.ExpiresAt = expiresAt;
        agent.Credential.RotatedAt = DateTimeOffset.UtcNow;
        // Rotation hands the new raw key back directly in this authenticated response (the caller
        // already proved possession of the old credential) rather than through the one-shot
        // collection poll, which exists only for the initial unauthenticated provisioning step.
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("AGENT_CREDENTIAL_ROTATED", "Agent", agentId.ToString(), agent.ClientName, cancellationToken);
        return Result<RotateAgentCredentialResponse>.Success(new RotateAgentCredentialResponse(rawKey, expiresAt));
    }

    private async Task LogFailureAsync(Guid agentId, string detail, string? clientIp, CancellationToken cancellationToken)
    {
        _db.AgentLogs.Add(new AgentLog { AgentId = agentId, EventType = AgentLogEventType.AuthenticationFailed, Detail = detail, IpAddress = clientIp });
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static string HashKey(string key)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var bytesA = System.Text.Encoding.UTF8.GetBytes(a);
        var bytesB = System.Text.Encoding.UTF8.GetBytes(b);
        return bytesA.Length == bytesB.Length && CryptographicOperations.FixedTimeEquals(bytesA, bytesB);
    }
}
