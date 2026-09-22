using Iemas.Domain.Common;
using Iemas.Domain.Email;

namespace Iemas.Domain.Cases;

/// <summary>
/// Requirements §38 — a Case may contain multiple related email messages. Join entity rather than
/// a single FK on EmailMessage because the matching decision (which signal matched, and why) is
/// itself worth recording per message for investigation (§89), not just "this message belongs to
/// this case."
/// </summary>
public class CaseEmail : Entity
{
    public Guid CaseId { get; set; }
    public Case Case { get; set; } = null!;

    public Guid EmailMessageId { get; set; }
    public EmailMessage EmailMessage { get; set; } = null!;

    /// <summary>Requirements §34/§89 — which signal associated this specific message with this Case.</summary>
    public CaseMatchSignal MatchSignal { get; set; }
    public string? MatchDetail { get; set; }
}
