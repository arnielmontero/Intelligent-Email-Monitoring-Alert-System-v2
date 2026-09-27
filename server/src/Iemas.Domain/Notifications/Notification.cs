using Iemas.Domain.Cases;
using Iemas.Domain.Common;
using Iemas.Domain.Identity;

namespace Iemas.Domain.Notifications;

/// <summary>Requirements §51 — the fixed set of predefined notification messages (no generic template builder).</summary>
public enum NotificationType
{
    NewEmail = 0,
    FirstReminder = 1,
    Reminder = 2,
    Overdue = 3,
    EscalationWarning = 4,
    Escalation = 5,
}

/// <summary>Requirements §104 — notification delivery lifecycle. Not every channel supports every state.</summary>
public enum NotificationDeliveryStatus
{
    Scheduled = 0,
    Queued = 1,
    Sent = 2,
    Delivered = 3,
    Displayed = 4,
    Acknowledged = 5,
    Cancelled = 6,
    Failed = 7,
    Expired = 8,
}

/// <summary>Requirements §51 — editable message text for one predefined notification. Timing and workflow stay in Reminder/Escalation policies.</summary>
public class NotificationTemplate : Entity
{
    public NotificationType Type { get; set; }
    public bool Enabled { get; set; } = true;
    public string Title { get; set; } = string.Empty;
    public string MessageText { get; set; } = string.Empty;

    public Guid? UpdatedByUserId { get; set; }
    public string? UpdatedByEmail { get; set; }
}

/// <summary>
/// Requirements §53/§104 — one notification sent (or attempted) to an Employee. A record of
/// "something happened", never itself Action Required work.
/// </summary>
public class Notification : Entity
{
    public Guid? CaseId { get; set; }
    public Case? Case { get; set; }

    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public NotificationType Type { get; set; }
    public NotificationDeliveryStatus Status { get; set; } = NotificationDeliveryStatus.Scheduled;

    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;

    /// <summary>Connected Agents the push was handed to. Zero means Queued: the Agent picks up current state on its next SYNC.</summary>
    public int DeliveredAgentCount { get; set; }

    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public string? FailureReason { get; set; }
}
