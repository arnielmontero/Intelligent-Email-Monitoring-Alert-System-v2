using Iemas.Domain.Common;

namespace Iemas.Domain.Audit;

/// <summary>
/// Requirements §67 — System Audit Log: "What did an administrator/configuration change?"
/// Append-only.
/// </summary>
public class AuditLog : Entity
{
    public Guid? UserId { get; set; }
    public string? UserEmail { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
}
