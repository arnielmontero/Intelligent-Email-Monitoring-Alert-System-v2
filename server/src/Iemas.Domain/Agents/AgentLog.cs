using Iemas.Domain.Common;

namespace Iemas.Domain.Agents;

/// <summary>
/// Requirements §67 "Technical Agent Log — what happened technically on the Agent? Connect,
/// Disconnect, Heartbeat, Authentication failure, Sync, Version, Error, Reconnect." Deliberately
/// separate from Case History (§66, "what happened to the Case") and the admin-facing AuditLog
/// (§67, "what did an administrator/configuration change") — mirrors the same three-way log
/// separation already established for EmailIntakeLog/AiClassificationLog. Never stores mailbox
/// credentials or Case content — technical/connection detail only.
/// </summary>
public class AgentLog : Entity
{
    public Guid AgentId { get; set; }
    public Agent Agent { get; set; } = null!;

    public AgentLogEventType EventType { get; set; }
    public string? Detail { get; set; }
    public string? IpAddress { get; set; }

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}
