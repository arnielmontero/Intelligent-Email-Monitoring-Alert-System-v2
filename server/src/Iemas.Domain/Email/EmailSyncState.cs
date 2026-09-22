using Iemas.Domain.Common;

namespace Iemas.Domain.Email;

/// <summary>
/// Requirements §20 ("processing pipeline must be restart-safe"), §75/§80 (reconciliation).
/// One row per monitored EmailAccount, tracking IMAP UIDVALIDITY/UID watermark so a restart or
/// a scheduled re-run resumes from the last successfully processed message instead of re-scanning
/// (or worse, re-processing) the whole mailbox. UIDVALIDITY changing means the server has
/// invalidated all UIDs (e.g. mailbox rebuilt) — the sync must restart from scratch in that case.
/// </summary>
public class EmailSyncState : Entity
{
    public Guid EmailAccountId { get; set; }
    public EmailAccount EmailAccount { get; set; } = null!;

    public uint? LastUidValidity { get; set; }
    public uint? LastSeenUid { get; set; }

    public DateTimeOffset? LastSyncStartedAt { get; set; }
    public DateTimeOffset? LastSyncCompletedAt { get; set; }
    public string? LastSyncError { get; set; }
    public int ConsecutiveFailureCount { get; set; }
}
