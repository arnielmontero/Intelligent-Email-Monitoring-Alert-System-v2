using Iemas.Domain.Common;
using Iemas.Domain.Email;
using Iemas.Domain.Identity;

namespace Iemas.Domain.Cases;

/// <summary>
/// Requirements §32 — the central business work record. Email is communication evidence; a Case
/// is the work topic it belongs to. A Case may contain multiple related email messages (§38) via
/// <see cref="CaseEmail"/>.
///
/// Ownership (§50) belongs to the Employee, resolved from the monitored <see cref="EmailAccount"/>'s
/// <see cref="EmailAccount.OwnerEmployeeId"/> at creation time — never from a computer/IP/Agent
/// instance, none of which exist as Case-owning concepts.
/// </summary>
public class Case : Entity
{
    /// <summary>Requirements §98 — human-readable display id, e.g. "CASE-000123". Sequential, assigned at creation, immutable.</summary>
    public string CaseNumber { get; set; } = string.Empty;

    public Guid EmailAccountId { get; set; }
    public EmailAccount EmailAccount { get; set; } = null!;

    /// <summary>Requirements §94 — sender email address is the strongest customer identity signal, not display name.</summary>
    public string CustomerEmailAddress { get; set; } = string.Empty;
    public string? CustomerDisplayName { get; set; }

    /// <summary>Requirements §50 — Case ownership is Employee-level, resolved from the account owner at creation. Nullable: an account may have no owner assigned yet.</summary>
    public Guid? OwnerEmployeeId { get; set; }
    public Employee? OwnerEmployee { get; set; }

    /// <summary>Original subject of the first/most representative email — for display only, never used alone for matching (§34 hard requirement).</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Requirements §35 — Re:/RE:/Fwd:/FW: stripped. A supporting signal only, never sufficient by itself for matching.</summary>
    public string NormalizedSubject { get; set; } = string.Empty;

    public CaseWorkStatus WorkStatus { get; set; } = CaseWorkStatus.New;
    public CaseReplyStatus ReplyStatus { get; set; } = CaseReplyStatus.NotApplicable;
    public CaseNotificationStatus NotificationStatus { get; set; } = CaseNotificationStatus.None;

    public DateTimeOffset FirstEmailReceivedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }

    /// <summary>Requirements §48 — required when WorkStatus transitions to Completed.</summary>
    public CaseCompletionReason? CompletionReason { get; set; }
    public string? CompletionComment { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Requirements §49/§36 — how many times this Case has been reopened after completion, for investigation (§89).</summary>
    public int ReopenCount { get; set; }

    public ICollection<CaseEmail> Emails { get; set; } = new List<CaseEmail>();
}
