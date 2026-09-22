using Iemas.Domain.Common;
using Iemas.Domain.Email;

namespace Iemas.Domain.Ai;

/// <summary>
/// Requirements §67 pattern (see <see cref="EmailIntakeLog"/>) — a technical per-attempt log for
/// the classification pipeline, separate from the admin-facing AuditLog. Never stores subject/body
/// content, matching the same sensitive-content-exclusion rule already verified for intake.
/// </summary>
public class AiClassificationLog : Entity
{
    public Guid EmailMessageId { get; set; }
    public EmailMessage EmailMessage { get; set; } = null!;

    public AiClassificationOutcome Outcome { get; set; }
    public string? Detail { get; set; }
    public string? AiModel { get; set; }
    public long DurationMs { get; set; }
}
