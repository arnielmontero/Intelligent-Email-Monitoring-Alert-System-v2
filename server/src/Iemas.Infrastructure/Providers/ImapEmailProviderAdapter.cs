using System.Diagnostics;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Common.Providers;
using Iemas.Domain.Email;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace Iemas.Infrastructure.Providers;

/// <summary>
/// Requirements §14.1 — IMAP provider adapter. Read-only end to end: opens the mailbox
/// ReadOnly, never sets flags/moves/deletes (Absolute system boundary — §2).
/// </summary>
public class ImapEmailProviderAdapter : IEmailProviderAdapter
{
    private const int ConnectTimeoutSeconds = 20;
    private const int FetchTimeoutSeconds = 120;

    /// <summary>A run stops starting new downloads after this long and resumes from its watermark next run.</summary>
    private static readonly TimeSpan FetchTimeBudget = TimeSpan.FromSeconds(45);
    private const int MaxConnectRetries = 2;
    private static readonly TimeSpan MaxConnectRetryDelay = TimeSpan.FromSeconds(10);

    private readonly IRetryDelay _retryDelay;

    public ImapEmailProviderAdapter(IRetryDelay retryDelay)
    {
        _retryDelay = retryDelay;
    }

    public EmailProtocol Protocol => EmailProtocol.Imap;

    public async Task<ProviderConnectionTestResult> TestConnectionAsync(EmailProviderConnectionSettings settings, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(ConnectTimeoutSeconds));

            using var client = await ConnectAndAuthenticateAsync(settings, timeoutCts.Token);
            await client.Inbox.OpenAsync(FolderAccess.ReadOnly, timeoutCts.Token);
            await client.DisconnectAsync(true, cancellationToken);

            stopwatch.Stop();
            return new ProviderConnectionTestResult(true, null, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            // Never include the secret in the error message; MailKit exceptions do not echo credentials,
            // but message text is still logged/stored, so keep it to protocol-level detail only.
            return new ProviderConnectionTestResult(false, ex.Message, stopwatch.Elapsed);
        }
    }

    public async Task<FetchInboxResult> FetchInboxMessagesAsync(
        EmailProviderConnectionSettings settings,
        uint? knownUidValidity,
        uint? afterUid,
        int maxMessages,
        CancellationToken cancellationToken,
        DateTimeOffset? deliveredAfter = null)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(FetchTimeoutSeconds));

        using var client = await ConnectAndAuthenticateAsync(settings, timeoutCts.Token);
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, timeoutCts.Token);

        var currentUidValidity = inbox.UidValidity;

        // §20 restart-safety: if the server's UIDVALIDITY changed since our last recorded
        // watermark, every previously-remembered UID is meaningless — the caller must treat
        // this as a full resync rather than trusting afterUid.
        var effectiveAfterUid = (knownUidValidity.HasValue && knownUidValidity.Value == currentUidValidity) ? afterUid : null;

        UniqueIdRange range = effectiveAfterUid.HasValue
            ? new UniqueIdRange(new UniqueId(currentUidValidity, effectiveAfterUid.Value + 1), UniqueId.MaxValue)
            : new UniqueIdRange(UniqueId.MinValue, UniqueId.MaxValue);

        var uids = await inbox.SearchAsync(SearchQuery.Uids(range), timeoutCts.Token);

        // With a cut-off, only mail delivered since then is downloaded; older mail is history and is
        // passed over rather than queued. IMAP SINCE is day-granular, so one day of slack is added —
        // the exact cut-off is still applied when each message is stored.
        IList<UniqueId> candidates = uids;
        if (deliveredAfter is DateTimeOffset since && uids.Count > 0)
        {
            candidates = await inbox.SearchAsync(
                SearchQuery.Uids(range).And(SearchQuery.DeliveredAfter(since.UtcDateTime.Date.AddDays(-1))), timeoutCts.Token);
        }
        var ordered = candidates.OrderBy(u => u.Id).ToList();
        var toFetch = ordered.Take(maxMessages).ToList();

        var messages = new List<ProviderMessage>();
        var malformed = new List<(uint Uid, string Error)>();
        uint? highestUidSeen = effectiveAfterUid;
        string? stoppedEarlyReason = null;
        var budget = Stopwatch.StartNew();

        foreach (var uid in toFetch)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (budget.Elapsed > FetchTimeBudget)
            {
                stoppedEarlyReason = $"Time budget reached after {messages.Count + malformed.Count} message(s); the rest continue on the next run.";
                break;
            }

            try
            {
                var mime = await inbox.GetMessageAsync(uid, timeoutCts.Token);
                messages.Add(NormalizeMessage(uid, mime));
            }
            catch (Exception ex) when (IsMessageLevelFailure(ex) && client.IsConnected)
            {
                // Requirements §20/§78 — one genuinely unreadable message must not abort the
                // batch, and must not block intake forever, so the watermark moves past it.
                malformed.Add((uid.Id, ex.Message));
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // A dropped connection or timeout says nothing about this message. Stop here and
                // leave the watermark on the last message actually handled, so this one and the
                // rest are fetched on the next run instead of being skipped for good (§81).
                stoppedEarlyReason = $"Stopped at message {uid.Id} ({ex.Message}); it and the rest continue on the next run.";
                break;
            }

            highestUidSeen = uid.Id;
        }

        // Every candidate handled: the watermark can move past the whole searched range, including
        // the skipped history, so the next run only looks at genuinely new mail.
        if (stoppedEarlyReason is null && toFetch.Count == ordered.Count && uids.Count > 0)
        {
            highestUidSeen = Math.Max(highestUidSeen ?? 0, uids.Max(u => u.Id));
        }

        if (client.IsConnected)
        {
            try
            {
                await client.DisconnectAsync(true, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The fetched messages are already in memory; a failed logout changes nothing.
            }
        }

        return new FetchInboxResult(currentUidValidity, highestUidSeen, messages, malformed, stoppedEarlyReason);
    }

    /// <summary>
    /// Only failures that are about the message itself: unparseable MIME, or the server refusing
    /// that one message. Connection, protocol and timeout failures are not the message's fault.
    /// </summary>
    public static bool IsMessageLevelFailure(Exception ex) =>
        ex is FormatException or ParseException or ImapCommandException;

    /// <summary>
    /// Requirements §42/§44 — reads the account's Sent folder for messages sent on/after
    /// <paramref name="since"/>, used by Reply Verification to look for a customer reply. A
    /// missing/inaccessible Sent folder — or any connection/auth failure — is reported via
    /// <see cref="FetchSentResult.FolderAccessible"/> = false rather than an empty message list,
    /// so the caller never confuses "couldn't check" with "checked, nothing there."
    /// </summary>
    public async Task<FetchSentResult> FetchSentMessagesAsync(
        EmailProviderConnectionSettings settings,
        DateTimeOffset since,
        int maxMessages,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(FetchTimeoutSeconds));

        ImapClient client;
        try
        {
            client = await ConnectAndAuthenticateAsync(settings, timeoutCts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // §44 — auth/connection failure must surface as "could not check," not as an empty
            // (and therefore falsely reassuring) Sent-folder result.
            return new FetchSentResult(false, ex.Message, Array.Empty<ProviderMessage>(), Array.Empty<(string, string)>());
        }
        using var _ = client;

        IMailFolder? sentFolder;
        try
        {
            sentFolder = await ResolveSentFolderAsync(client, timeoutCts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await SafeDisconnectAsync(client, cancellationToken);
            return new FetchSentResult(false, ex.Message, Array.Empty<ProviderMessage>(), Array.Empty<(string, string)>());
        }

        if (sentFolder is null)
        {
            await SafeDisconnectAsync(client, cancellationToken);
            return new FetchSentResult(false, "No Sent folder could be located on this account (server does not advertise SPECIAL-USE \\Sent and no conventional folder name matched).", Array.Empty<ProviderMessage>(), Array.Empty<(string, string)>());
        }

        try
        {
            await sentFolder.OpenAsync(FolderAccess.ReadOnly, timeoutCts.Token);

            var uids = await sentFolder.SearchAsync(SearchQuery.SentSince(since.UtcDateTime), timeoutCts.Token);
            var toFetch = uids.OrderByDescending(u => u.Id).Take(maxMessages).ToList();

            var messages = new List<ProviderMessage>();
            var malformed = new List<(string Identifier, string Error)>();

            foreach (var uid in toFetch)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var mime = await sentFolder.GetMessageAsync(uid, timeoutCts.Token);
                    messages.Add(NormalizeMessage(uid, mime));
                }
                catch (Exception ex)
                {
                    // §42 — one malformed Sent message must not abort the whole verification
                    // check, same isolation principle as malformed Inbox messages (§20/§78).
                    malformed.Add((uid.Id.ToString(), ex.Message));
                }
            }

            await client.DisconnectAsync(true, cancellationToken);
            return new FetchSentResult(true, null, messages, malformed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await SafeDisconnectAsync(client, cancellationToken);
            return new FetchSentResult(false, ex.Message, Array.Empty<ProviderMessage>(), Array.Empty<(string, string)>());
        }
    }

    /// <summary>
    /// Tries the IMAP SPECIAL-USE \Sent attribute first (the reliable, server-advertised way to
    /// find the Sent folder regardless of naming/locale), then falls back to a short list of
    /// conventional names for servers that don't support SPECIAL-USE/XLIST.
    /// </summary>
    private static async Task<IMailFolder?> ResolveSentFolderAsync(ImapClient client, CancellationToken cancellationToken)
    {
        if (client.Capabilities.HasFlag(ImapCapabilities.SpecialUse))
        {
            var special = client.GetFolder(SpecialFolder.Sent);
            if (special is not null) return special;
        }

        foreach (var name in ConventionalSentFolderNames)
        {
            try
            {
                return await client.GetFolderAsync(name, cancellationToken);
            }
            catch (FolderNotFoundException)
            {
                // Try the next conventional name.
            }
        }

        return null;
    }

    private static readonly string[] ConventionalSentFolderNames = { "Sent", "Sent Items", "Sent Mail", "[Gmail]/Sent Mail" };

    private static async Task SafeDisconnectAsync(ImapClient client, CancellationToken cancellationToken)
    {
        try
        {
            if (client.IsConnected)
            {
                await client.DisconnectAsync(true, cancellationToken);
            }
        }
        catch
        {
            // Best-effort cleanup only; the original failure is what the caller needs to see.
        }
    }

    /// <summary>
    /// Phase 10 hardening — bounded retry around connect/authenticate only, never around the
    /// per-message fetch that follows (a message-level failure is already isolated per-message,
    /// see <see cref="FetchInboxMessagesAsync"/>/<see cref="FetchSentMessagesAsync"/>). Retrying a
    /// failed connect attempt on the same <see cref="ImapClient"/> instance risks stale TLS/socket
    /// state, so a failed attempt disposes its client and the next attempt (if any) constructs a
    /// fresh one — the caller always receives either a connected, authenticated client or the
    /// original exception, never a half-connected one. <see cref="ImapFailureClassifier"/> decides
    /// whether the failure is worth retrying at all (never for bad credentials) and how many times.
    /// </summary>
    private async Task<ImapClient> ConnectAndAuthenticateAsync(EmailProviderConnectionSettings settings, CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        for (var attempt = 0; attempt <= MaxConnectRetries; attempt++)
        {
            var client = new ImapClient();
            try
            {
                await ConnectAndAuthenticateOnceAsync(client, settings, cancellationToken);
                return client;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                client.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                client.Dispose();
                lastException = ex;

                var category = ImapFailureClassifier.Classify(ex);
                if (!ImapFailureClassifier.IsRetryable(category, attempt) || attempt == MaxConnectRetries)
                {
                    throw;
                }

                var delay = ComputeConnectRetryDelay(attempt);
                await _retryDelay.WaitAsync(delay, cancellationToken);
            }
        }

        // Unreachable — the loop always either returns or throws — but keeps the compiler happy
        // about every code path producing a value.
        throw lastException ?? new InvalidOperationException("IMAP connect retry loop exited without a result.");
    }

    private static TimeSpan ComputeConnectRetryDelay(int attemptIndex)
    {
        var exponential = TimeSpan.FromSeconds(Math.Pow(2, attemptIndex));
        return exponential > MaxConnectRetryDelay ? MaxConnectRetryDelay : exponential;
    }

    private static async Task ConnectAndAuthenticateOnceAsync(ImapClient client, EmailProviderConnectionSettings settings, CancellationToken cancellationToken)
    {
        var secureSocketOptions = MapEncryption(settings.Encryption);
        await client.ConnectAsync(settings.Host, settings.Port, secureSocketOptions, cancellationToken);

        if (settings.AuthMethod == EmailAuthMethod.OAuth2)
        {
            await client.AuthenticateAsync(new SaslMechanismOAuth2(settings.Username, settings.Secret), cancellationToken);
        }
        else
        {
            await client.AuthenticateAsync(settings.Username, settings.Secret, cancellationToken);
        }
    }

    private static ProviderMessage NormalizeMessage(UniqueId uid, MimeMessage mime)
    {
        var from = mime.From.Mailboxes.FirstOrDefault();
        var to = mime.To.Mailboxes.Select(m => m.Address).ToList();
        var cc = mime.Cc.Mailboxes.Select(m => m.Address).ToList();

        var references = mime.References?.ToList() ?? new List<string>();

        var attachments = mime.Attachments
            .OfType<MimePart>()
            .Select(part => new ProviderAttachmentSummary(
                part.FileName ?? "unnamed",
                part.ContentType?.MimeType,
                part.Content?.Stream?.Length ?? 0))
            .ToList();

        return new ProviderMessage(
            uid.Id.ToString(),
            mime.MessageId,
            null, // IMAP has no first-class thread ID; Phase 5 Case Matching derives threading from InReplyTo/References (§34).
            mime.InReplyTo,
            references,
            from?.Address ?? string.Empty,
            from?.Name,
            to,
            cc,
            mime.Subject ?? string.Empty,
            mime.TextBody,
            mime.HtmlBody,
            // Normalize to UTC at the provider boundary. The sender's Date header carries their
            // local offset (e.g. +08:00), and PostgreSQL's timestamptz requires UTC-offset values
            // via Npgsql — discovered live when a real message with a non-UTC Date header crashed
            // persistence (see Phase 3 bugs log in the progress tracker). Storing local-normalized-
            // to-UTC also means every ReceivedAt in the database is directly comparable regardless
            // of which timezone a customer's mail client used.
            mime.Date != default ? mime.Date.ToUniversalTime() : DateTimeOffset.UtcNow,
            attachments);
    }

    private static SecureSocketOptions MapEncryption(string encryption) => encryption.Trim().ToUpperInvariant() switch
    {
        "SSL/TLS" or "SSL" or "TLS" => SecureSocketOptions.SslOnConnect,
        "STARTTLS" => SecureSocketOptions.StartTls,
        "NONE" => SecureSocketOptions.None,
        _ => SecureSocketOptions.Auto
    };
}
