using Iemas.Domain.Agents;

namespace Iemas.Application.Agents.Dtos;

/// <summary>§68 — the exact "Client registration information" fields the Agent submits.</summary>
public record RegisterAgentRequest(
    string EmailAddress,
    string ClientName,
    string? ServerAddress,
    string? AgentVersion,
    string? DeviceMetadata);

/// <summary>
/// Returned immediately on registration. <see cref="RegistrationRequestToken"/> is the opaque
/// handle the Agent polls status with — it is NOT a credential and grants no API access; it only
/// lets the Agent ask "has an admin approved me yet." §68/§70 — the Agent never receives, chooses,
/// or generates the actual Registration Key from this call.
/// </summary>
public record RegisterAgentResponse(Guid AgentId, string RegistrationRequestToken, AgentRegistrationStatus Status);

/// <summary>
/// §68 — what the Agent polls to learn its status and, exactly once, collect its
/// server-generated key. <see cref="RegistrationKey"/> is non-null only on the single poll call
/// that occurs after approval and before the key has ever been returned; every call after that
/// returns null here even though <see cref="Status"/> stays Approved — collection is one-shot.
/// </summary>
public record AgentRegistrationStatusResponse(
    AgentRegistrationStatus Status,
    string? RegistrationKey,
    DateTimeOffset? RegistrationKeyExpiresAt,
    string? RejectionReason);

public record ApproveAgentRequest(Guid EmployeeId);
public record RejectAgentRequest(string Reason);
public record RevokeAgentRequest(string Reason);

public record AgentDto(
    Guid Id,
    string ClientName,
    string EnrollmentEmailAddress,
    Guid? EmployeeId,
    string? EmployeeName,
    string? ServerAddress,
    string? LastKnownClientIp,
    string? AgentVersion,
    AgentRegistrationStatus RegistrationStatus,
    AgentConnectionStatus ConnectionStatus,
    DateTimeOffset RegisteredAt,
    Guid? ApprovedByUserId,
    string? ApprovedByUserEmail,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? LastConnectedAt,
    DateTimeOffset? LastHeartbeatAt);

public record AgentLogDto(Guid Id, AgentLogEventType EventType, string? Detail, string? IpAddress, DateTimeOffset OccurredAt);
