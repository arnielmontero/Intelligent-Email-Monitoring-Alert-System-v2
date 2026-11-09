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

    /// <summary>When the Sent folder was last checked for a reply to this Case (every check, even when the result didn't change).</summary>
    public DateTimeOffset? ReplyLastCheckedAt { get; set; }

    public ICollection<CaseEmail> Emails { get; set; } = new List<CaseEmail>();

    // ---- Inquiry supervision (inquiry-supervision/PROGRESS.md Phases 2-5) ----

    /// <summary>Where the inquiry is in its lifecycle. Only Resolved/Closed end supervision.</summary>
    public InquiryStage Stage { get; set; } = InquiryStage.AwaitingInitialResponse;

    /// <summary>When the current stage began - the time of the email or action that caused it. Every stage timer counts from here.</summary>
    public DateTimeOffset StageChangedAt { get; set; }

    /// <summary>The stage before PotentiallyResolved, so an agent rejecting the AI's suggestion returns the inquiry to where it was.</summary>
    public InquiryStage? StageBeforePotentiallyResolved { get; set; }

    public DateTimeOffset? FirstAgentResponseAt { get; set; }
    public DateTimeOffset? LastCustomerMessageAt { get; set; }
    public DateTimeOffset? LastAgentMessageAt { get; set; }
    public DateTimeOffset? LastInternalActivityAt { get; set; }

    /// <summary>Latest Sent-folder message already stored for this inquiry; the next sync only looks after it.</summary>
    public DateTimeOffset? ConversationSyncedThrough { get; set; }

    /// <summary>When the current supervision cycle began: the first email, or the customer email that reopened a closed inquiry. The resolution target counts from here.</summary>
    public DateTimeOffset? SupervisionCycleStartedAt { get; set; }

    /// <summary>Overall resolution target (business days from the cycle start). A target only: never closes the inquiry.</summary>
    public DateTimeOffset? ResolutionDueAt { get; set; }
    public DateTimeOffset? OverdueSince { get; set; }
    public DateTimeOffset? OverdueEscalatedAt { get; set; }

    // Per-stage "episode" counters - reset whenever the stage changes, so a new customer message or
    // agent reply starts a fresh, fair timer instead of inheriting old reminders.
    public DateTimeOffset? EpisodeDeadlineMissedAt { get; set; }
    public DateTimeOffset? EpisodeWaitNoticeAt { get; set; }
    public int EpisodeRemindersSent { get; set; }
    public DateTimeOffset? EpisodeLastReminderAt { get; set; }
    public int EpisodeEscalations { get; set; }
    public DateTimeOffset? EpisodeLastEscalatedAt { get; set; }

    // AI resolution assessment (Phase 5) - advisory; can only ever move the inquiry to PotentiallyResolved.
    public ResolutionVerdict? ResolutionVerdict { get; set; }
    public double? ResolutionConfidence { get; set; }
    public string? ResolutionReason { get; set; }
    public DateTimeOffset? ResolutionAssessedAt { get; set; }

    /// <summary>How many conversation messages the last assessment saw; a new assessment runs only when the conversation changed.</summary>
    public int ResolutionAssessedMessageCount { get; set; }
}
