using Iemas.Domain.Email;

namespace Iemas.Application.Common.Providers;

/// <summary>
/// Connection parameters needed to talk to a mailbox, resolved from an <see cref="EmailAccount"/>
/// plus its decrypted credential. Never persisted or logged — constructed on demand and discarded.
/// </summary>
public record EmailProviderConnectionSettings(
    EmailProtocol Protocol,
    string Host,
    int Port,
    string Encryption,
    string Username,
    EmailAuthMethod AuthMethod,
    string Secret);

public record ProviderConnectionTestResult(bool Succeeded, string? ErrorMessage, TimeSpan Duration);

public record ProviderAttachmentSummary(string FileName, string? ContentType, long SizeBytes);

/// <summary>
/// A single normalized message as read from the provider. Deliberately provider-agnostic —
/// no MailKit/Graph SDK types appear here, so Phase 4/5 consumers never need to know which
/// provider a message came from.
/// </summary>
public record ProviderMessage(
    string ProviderMessageId,
    string? MessageId,
    string? ThreadId,
    string? InReplyTo,
    IReadOnlyCollection<string> References,
    string FromAddress,
    string? FromDisplayName,
    IReadOnlyCollection<string> ToAddresses,
    IReadOnlyCollection<string> CcAddresses,
    string Subject,
    string? BodyText,
    string? BodyHtml,
    DateTimeOffset ReceivedAt,
    IReadOnlyCollection<ProviderAttachmentSummary> Attachments);

/// <summary>
/// Requirements §20 (restart-safe pipeline) — fetch is watermark-based (IMAP UID), not
/// date-range or "fetch everything," so a restart resumes instead of re-scanning the mailbox.
/// UidValidity must be checked by the caller against the last known value before trusting
/// LastUid; if it changed, the server has invalidated all UIDs and the watermark must reset.
/// </summary>
public record FetchInboxResult(
    uint UidValidity,
    uint? HighestUidSeen,
    IReadOnlyCollection<ProviderMessage> Messages,
    IReadOnlyCollection<(uint Uid, string Error)> MalformedMessages);

/// <summary>
/// Requirements §42 — Reply Verification reads the account's own Sent folder (the employee
/// replies through their normal mail client against the same mailbox IEMAS monitors; this is not
/// the separate §18 outbound *notification* account). Deliberately its own result/method rather
/// than reusing <see cref="FetchInboxResult"/>/<see cref="IEmailProviderAdapter.FetchInboxMessagesAsync"/>
/// — Inbox reading and Sent reading are kept as clearly separate capabilities at the interface
/// level (different folder, different purpose, different caller), even though both are read-only
/// IMAP fetches under the hood. A provider adapter that cannot access Sent (e.g. a future
/// provider without folder access) can report that here without touching Inbox fetch at all.
/// </summary>
public record FetchSentResult(
    bool FolderAccessible,
    string? AccessError,
    IReadOnlyCollection<ProviderMessage> Messages,
    IReadOnlyCollection<(string Identifier, string Error)> MalformedMessages);

/// <summary>
/// Requirements §14.1 — email provider-specific logic must be isolated behind adapters.
/// The Workflow Engine (Case matching, classification, reply verification) must never depend on
/// IMAP/Graph/etc. specifics directly; it only depends on this interface.
/// </summary>
public interface IEmailProviderAdapter
{
    EmailProtocol Protocol { get; }

    Task<ProviderConnectionTestResult> TestConnectionAsync(EmailProviderConnectionSettings settings, CancellationToken cancellationToken);

    /// <summary>
    /// Fetches messages with UID greater than <paramref name="afterUid"/> (null = fetch from the
    /// start of the mailbox). Read-only — never marks messages as read, moves, or deletes
    /// anything (Absolute system boundary, §2). A malformed individual message must not abort
    /// the whole fetch; it is reported in <see cref="FetchInboxResult.MalformedMessages"/> instead.
    /// </summary>
    Task<FetchInboxResult> FetchInboxMessagesAsync(
        EmailProviderConnectionSettings settings,
        uint? knownUidValidity,
        uint? afterUid,
        int maxMessages,
        CancellationToken cancellationToken);

    /// <summary>
    /// Requirements §42/§44 — reads the account's Sent folder within a bounded recent window
    /// (verification only ever needs to look for a reply sent after a specific Case's messages
    /// arrived, never the whole Sent history). Read-only, same as Inbox (§2). A mailbox/folder
    /// that cannot be opened (auth failure, folder missing, timeout) must be reported via
    /// <see cref="FetchSentResult.FolderAccessible"/> = false with <see cref="FetchSentResult.AccessError"/>
    /// set — this is the signal the caller uses to distinguish "genuinely checked, no reply found"
    /// from "could not check at all" (§44: never assume no reply from a mailbox that couldn't be
    /// inspected). A malformed individual Sent message must not abort the whole fetch, same as Inbox.
    /// </summary>
    Task<FetchSentResult> FetchSentMessagesAsync(
        EmailProviderConnectionSettings settings,
        DateTimeOffset since,
        int maxMessages,
        CancellationToken cancellationToken);
}
