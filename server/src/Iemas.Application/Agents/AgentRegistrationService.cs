using System.Security.Cryptography;
using Iemas.Application.Agents.Dtos;
using Iemas.Application.Common;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Common.Security;
using Iemas.Domain.Agents;
using Iemas.Domain.Email;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Agents;

/// <summary>
/// Requirements §68 (Agent Registration process), §69 (Agent Identity), §70 (Credential
/// Requirements), §71 (Registration/Connection status). Implements the registration state machine
/// exactly as diagrammed: Agent Installed → Registration Request → PENDING → Admin Review →
/// APPROVE → Server Generates Credential → Provisioned to Agent → Agent Stores → Authenticates →
/// CONNECTED.
///
/// The critical invariant this service exists to enforce (per this phase's explicit instruction):
/// only the server ever generates the Registration Key (<see cref="IAgentTokenService.GenerateRegistrationKey"/>,
/// called only from <see cref="ApproveAsync"/>); only an approved enrollment ever receives one; the
/// client automatically records it via a poll rather than manual entry; the raw key is returned at
/// most once, from <see cref="GetRegistrationStatusAsync"/>, and never again after that single call
/// — and is never persisted in plaintext even during the window before collection (§16-style
/// AES-256-GCM encryption at rest via <see cref="ICredentialEncryptionService"/>, the same service
/// used for mailbox credentials).
/// </summary>
public class AgentRegistrationService
{
    private readonly IAppDbContext _db;
    private readonly IAgentTokenService _agentTokenService;
    private readonly ICredentialEncryptionService _encryptionService;
    private readonly IAuditService _auditService;

    public AgentRegistrationService(
        IAppDbContext db,
        IAgentTokenService agentTokenService,
        ICredentialEncryptionService encryptionService,
        IAuditService auditService)
    {
        _db = db;
        _agentTokenService = agentTokenService;
        _encryptionService = encryptionService;
        _auditService = auditService;
    }

    /// <summary>§68 "Agent Installed → Agent Sends Registration Request → Server Creates PENDING."</summary>
    public async Task<Result<RegisterAgentResponse>> RegisterAsync(RegisterAgentRequest request, string? clientIp, CancellationToken cancellationToken)
    {
        var email = request.EmailAddress.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            return Result<RegisterAgentResponse>.Failure("A valid email address is required.");
        }

        if (string.IsNullOrWhiteSpace(request.ClientName))
        {
            return Result<RegisterAgentResponse>.Failure("A client/agent name is required.");
        }

        var clientName = request.ClientName.Trim();
        if (InputSanitizer.ValidateFreeText("Client name", clientName) is { } clientNameError)
        {
            return Result<RegisterAgentResponse>.Failure(clientNameError);
        }

        // §68 "Email must correspond to an enrolled IEMAS email account." Checked against Inbound
        // accounts specifically — an Agent represents an employee monitoring their own mailbox
        // activity, not an arbitrary email address.
        var accountExists = await _db.EmailAccounts
            .AnyAsync(a => a.EmailAddress == email && a.Purpose == EmailAccountPurpose.Inbound, cancellationToken);
        if (!accountExists)
        {
            return Result<RegisterAgentResponse>.Failure("This email address does not correspond to an enrolled IEMAS email account.");
        }

        var agent = new Agent
        {
            ClientName = clientName,
            EnrollmentEmailAddress = email,
            ServerAddress = request.ServerAddress,
            AgentVersion = request.AgentVersion,
            DeviceMetadata = request.DeviceMetadata,
            LastKnownClientIp = clientIp,
            RegistrationStatus = AgentRegistrationStatus.Pending,
            RegistrationRequestToken = GenerateOpaqueToken(),
        };

        _db.Agents.Add(agent);
        await _db.SaveChangesAsync(cancellationToken);

        _db.AgentLogs.Add(new AgentLog
        {
            AgentId = agent.Id,
            EventType = AgentLogEventType.RegistrationRequested,
            Detail = $"{agent.ClientName} ({email})",
            IpAddress = clientIp,
        });
        await _db.SaveChangesAsync(cancellationToken);

        // §67 "Technical Agent Log," not the admin-facing AuditLog — no administrator acted yet.
        return Result<RegisterAgentResponse>.Success(new RegisterAgentResponse(agent.Id, agent.RegistrationRequestToken, agent.RegistrationStatus));
    }

    /// <summary>
    /// §68 "Agent Authenticates" precursor — the Agent polls this with its own opaque
    /// RegistrationRequestToken (not the Agent ID alone, so a guessed/enumerated ID cannot be used
    /// to probe registration status). Returns the raw key exactly once, decrypting the transient
    /// AES-256-GCM-protected value and immediately clearing it from storage so it can never be
    /// served again even to a legitimate repeat request.
    /// </summary>
    public async Task<Result<AgentRegistrationStatusResponse>> GetRegistrationStatusAsync(string registrationRequestToken, CancellationToken cancellationToken)
    {
        var agent = await _db.Agents
            .Include(a => a.Credential)
            .FirstOrDefaultAsync(a => a.RegistrationRequestToken == registrationRequestToken, cancellationToken);

        if (agent is null)
        {
            return Result<AgentRegistrationStatusResponse>.Failure("Registration request not found.");
        }

        var credential = agent.Credential;
        if (agent.RegistrationStatus != AgentRegistrationStatus.Approved
            || credential is null
            || credential.RawKeyCollected
            || credential.PendingKeyCiphertext is null)
        {
            // Not yet approved, or already collected once — never return the key again (one-shot
            // collection is the mechanism that makes "automatic receipt/storage, never manual
            // entry" verifiable: there is exactly one moment the raw value exists in a response).
            return Result<AgentRegistrationStatusResponse>.Success(new AgentRegistrationStatusResponse(
                agent.RegistrationStatus, null, null, agent.RejectionReason));
        }

        var rawKey = _encryptionService.Decrypt(new EncryptedSecret(
            credential.PendingKeyCiphertext, credential.PendingKeyNonce!, credential.PendingKeyTag!, credential.PendingKeyEncryptionKeyId!));

        credential.RawKeyCollected = true;
        credential.PendingKeyCiphertext = null;
        credential.PendingKeyNonce = null;
        credential.PendingKeyTag = null;
        credential.PendingKeyEncryptionKeyId = null;
        await _db.SaveChangesAsync(cancellationToken);

        _db.AgentLogs.Add(new AgentLog
        {
            AgentId = agent.Id,
            EventType = AgentLogEventType.Sync,
            Detail = "Registration Key collected by Agent.",
        });
        await _db.SaveChangesAsync(cancellationToken);

        return Result<AgentRegistrationStatusResponse>.Success(new AgentRegistrationStatusResponse(
            agent.RegistrationStatus, rawKey, credential.ExpiresAt, null));
    }

    /// <summary>§68 "Administrator Reviews → APPROVE → Server Generates Unique Credential."</summary>
    public async Task<Result<bool>> ApproveAsync(Guid agentId, ApproveAgentRequest request, Guid approvedByUserId, CancellationToken cancellationToken)
    {
        var agent = await _db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is null)
        {
            return Result<bool>.Failure("Agent registration not found.");
        }

        if (agent.RegistrationStatus != AgentRegistrationStatus.Pending)
        {
            return Result<bool>.Failure($"Agent registration is already {agent.RegistrationStatus}.");
        }

        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == request.EmployeeId && e.IsActive, cancellationToken);
        if (employee is null)
        {
            return Result<bool>.Failure("Employee not found or inactive.");
        }

        // §68 "Email must correspond to an enrolled IEMAS email account" — re-validated at
        // approval time against the specific Employee being assigned, not just any account.
        var employeeOwnsAccount = await _db.EmailAccounts.AnyAsync(
            a => a.EmailAddress == agent.EnrollmentEmailAddress && a.OwnerEmployeeId == employee.Id, cancellationToken);
        if (!employeeOwnsAccount)
        {
            var account = await _db.EmailAccounts.AsNoTracking()
                .Where(a => a.EmailAddress == agent.EnrollmentEmailAddress)
                .Select(a => new { OwnerName = a.OwnerEmployee != null ? a.OwnerEmployee.FullName : null })
                .FirstOrDefaultAsync(cancellationToken);
            return Result<bool>.Failure(account is null
                ? $"No email account {agent.EnrollmentEmailAddress} exists in IEMAS. Add it on the Email Accounts page with {employee.FullName} as owner, then approve."
                : account.OwnerName is null
                    ? $"The email account {agent.EnrollmentEmailAddress} has no owner. On the Email Accounts page, Edit it and set the owner to {employee.FullName}, then approve."
                    : $"The email account {agent.EnrollmentEmailAddress} is owned by {account.OwnerName}, not {employee.FullName}. Approve it for {account.OwnerName}, or change the account's owner on the Email Accounts page.");
        }

        agent.EmployeeId = employee.Id;
        agent.RegistrationStatus = AgentRegistrationStatus.Approved;
        agent.ApprovedAt = DateTimeOffset.UtcNow;
        agent.ApprovedByUserId = approvedByUserId;

        // §70 — generated only here, only by the server. Never accepts a caller-supplied value.
        var (rawKey, expiresAt) = _agentTokenService.GenerateRegistrationKey();
        var encrypted = _encryptionService.Encrypt(rawKey);

        // Explicit Add rather than assigning through the Agent.Credential navigation — the Agent
        // row here is pre-existing (loaded above), not newly tracked in this call, so EF Core
        // needs the new AgentCredential added explicitly (same pattern as EmailAccountService's
        // update-path credential rotation, as opposed to its create-path where account and
        // credential are both new together).
        var credential = new AgentCredential
        {
            AgentId = agent.Id,
            KeyHash = HashKey(rawKey),
            ExpiresAt = expiresAt,
            PendingKeyCiphertext = encrypted.Ciphertext,
            PendingKeyNonce = encrypted.Nonce,
            PendingKeyTag = encrypted.Tag,
            PendingKeyEncryptionKeyId = encrypted.KeyId,
        };
        _db.AgentCredentials.Add(credential);

        await _db.SaveChangesAsync(cancellationToken);

        _db.AgentLogs.Add(new AgentLog { AgentId = agent.Id, EventType = AgentLogEventType.Approved, Detail = $"Approved for {employee.FullName}" });
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("AGENT_APPROVED", "Agent", agentId.ToString(), $"{agent.ClientName} ({agent.EnrollmentEmailAddress}) -> {employee.FullName}", cancellationToken);
        return Result<bool>.Success(true);
    }

    public async Task<Result<bool>> RejectAsync(Guid agentId, RejectAgentRequest request, CancellationToken cancellationToken)
    {
        var agent = await _db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is null) return Result<bool>.Failure("Agent registration not found.");
        if (agent.RegistrationStatus != AgentRegistrationStatus.Pending)
        {
            return Result<bool>.Failure($"Agent registration is already {agent.RegistrationStatus}.");
        }

        agent.RegistrationStatus = AgentRegistrationStatus.Rejected;
        agent.RejectedAt = DateTimeOffset.UtcNow;
        agent.RejectionReason = request.Reason;
        await _db.SaveChangesAsync(cancellationToken);

        _db.AgentLogs.Add(new AgentLog { AgentId = agent.Id, EventType = AgentLogEventType.Rejected, Detail = request.Reason });
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("AGENT_REJECTED", "Agent", agentId.ToString(), request.Reason, cancellationToken);
        return Result<bool>.Success(true);
    }

    /// <summary>§70 "Revocable. Invalid after revocation." Works on any non-revoked Agent regardless of current status.</summary>
    public async Task<Result<bool>> RevokeAsync(Guid agentId, RevokeAgentRequest request, CancellationToken cancellationToken)
    {
        var agent = await _db.Agents.Include(a => a.Credential).FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is null) return Result<bool>.Failure("Agent registration not found.");
        if (agent.RegistrationStatus == AgentRegistrationStatus.Revoked)
        {
            return Result<bool>.Failure("Agent registration is already revoked.");
        }

        agent.RegistrationStatus = AgentRegistrationStatus.Revoked;
        agent.RevokedAt = DateTimeOffset.UtcNow;
        agent.RevocationReason = request.Reason;
        agent.ConnectionStatus = AgentConnectionStatus.Disconnected;

        if (agent.Credential is not null)
        {
            agent.Credential.RevokedAt = DateTimeOffset.UtcNow;
            // §16-style — a revoked key must never linger recoverable in the ciphertext window either.
            agent.Credential.PendingKeyCiphertext = null;
            agent.Credential.PendingKeyNonce = null;
            agent.Credential.PendingKeyTag = null;
            agent.Credential.PendingKeyEncryptionKeyId = null;
        }

        await _db.SaveChangesAsync(cancellationToken);

        _db.AgentLogs.Add(new AgentLog { AgentId = agent.Id, EventType = AgentLogEventType.Revoked, Detail = request.Reason });
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("AGENT_REVOKED", "Agent", agentId.ToString(), request.Reason, cancellationToken);
        return Result<bool>.Success(true);
    }

    private static string HashKey(string key)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes);
    }

    private static string GenerateOpaqueToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
