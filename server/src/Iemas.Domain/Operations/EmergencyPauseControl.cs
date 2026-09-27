using Iemas.Domain.Common;

namespace Iemas.Domain.Operations;

/// <summary>Requirements §91 — the five Emergency Pause controls.</summary>
public enum PauseControl
{
    EmailProcessing = 0,
    AiClassification = 1,
    Reminders = 2,
    Escalations = 3,
    AgentNotifications = 4,
}

/// <summary>
/// Requirements §91 — current state of one pause control. Every change is also written to the
/// audit log; this row only holds the latest state. Pausing never deletes Cases: engines simply
/// skip their run and pick up where they left off on resume.
/// </summary>
public class EmergencyPauseControl : Entity
{
    public PauseControl Control { get; set; }
    public bool IsPaused { get; set; }
    public string? Reason { get; set; }

    public Guid? ChangedByUserId { get; set; }
    public string? ChangedByEmail { get; set; }
    public DateTimeOffset? ChangedAt { get; set; }
}
