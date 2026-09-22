using Iemas.Domain.Common;

namespace Iemas.Domain.Email;

/// <summary>
/// Requirements §67 draws a hard line between "System Audit Log" (admin/config changes) and
/// technical processing logs. Intake is a technical pipeline, so its per-attempt outcomes get
/// their own log rather than being mixed into AuditLog. Never stores message body/subject —
/// only identifiers and outcome, so this table itself isn't a place sensitive content leaks into.
/// </summary>
public enum EmailIntakeOutcome
{
    Fetched = 0,
    Skipped_Duplicate = 1,
    Skipped_Malformed = 2,
    Failed = 3,
}

public class EmailIntakeLog : Entity
{
    public Guid EmailAccountId { get; set; }
    public EmailAccount EmailAccount { get; set; } = null!;

    public string? ProviderMessageId { get; set; }
    public EmailIntakeOutcome Outcome { get; set; }
    public string? Detail { get; set; }
    public long DurationMs { get; set; }
}
