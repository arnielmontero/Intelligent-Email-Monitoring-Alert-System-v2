using Iemas.Domain.Common;
using Iemas.Domain.Identity;

namespace Iemas.Domain.Agents;

/// <summary>
/// Requirements §68-§71 — a Windows Client Agent installation. Deliberately a third identity
/// concept alongside <see cref="User"/> (CMS login) and <see cref="Employee"/> (the person) —
/// exactly the distinction <see cref="Employee"/>'s own doc comment anticipates. An Employee may
/// have multiple Agents (§13); a Case is never owned by an Agent (§50), only by an Employee.
///
/// §69 — Agent identity is the server-issued <see cref="Entity.Id"/> (Agent ID) plus its
/// <see cref="AgentCredential"/>, never email address/IP/client name alone; those remain metadata
/// on this record for CMS display and investigation only (§71).
/// </summary>
public class Agent : Entity
{
    /// <summary>§68 registration info — employee-facing display name for this specific installation (e.g. "Office PC").</summary>
    public string ClientName { get; set; } = string.Empty;

    /// <summary>§68 — the email address supplied at registration; must correspond to an enrolled IEMAS email account, resolved to <see cref="EmployeeId"/> at approval time.</summary>
    public string EnrollmentEmailAddress { get; set; } = string.Empty;

    /// <summary>Resolved Employee this Agent belongs to once registration succeeds (§11 — Employee/Agent are separate, an Employee may own several Agents).</summary>
    public Guid? EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    /// <summary>§68 — the IEMAS server address the Agent reports connecting to (self-reported, for support/diagnostics — never trusted as identity, §69).</summary>
    public string? ServerAddress { get; set; }

    /// <summary>§68/§69 — client IP is metadata only, never identity. Updated on every successful authentication.</summary>
    public string? LastKnownClientIp { get; set; }

    public string? AgentVersion { get; set; }

    /// <summary>§68 "Device metadata" — free-form JSON (OS version, machine name, etc.), opaque to the server beyond storage/display.</summary>
    public string? DeviceMetadata { get; set; }

    public AgentRegistrationStatus RegistrationStatus { get; set; } = AgentRegistrationStatus.Pending;
    public AgentConnectionStatus ConnectionStatus { get; set; } = AgentConnectionStatus.Disconnected;

    public DateTimeOffset RegisteredAt { get; set; } = DateTimeOffset.UtcNow;

    public Guid? ApprovedByUserId { get; set; }
    public User? ApprovedByUser { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }

    public string? RejectionReason { get; set; }
    public DateTimeOffset? RejectedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevocationReason { get; set; }

    public DateTimeOffset? LastConnectedAt { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }

    /// <summary>
    /// §68 — the one-time collection handle the Agent polls with to receive its provisioned
    /// credential after approval. Opaque, unguessable (see AgentRegistrationService), never the
    /// credential itself. Cleared once the credential has been collected (single collection only).
    /// </summary>
    public string RegistrationRequestToken { get; set; } = string.Empty;

    public AgentCredential? Credential { get; set; }
}
