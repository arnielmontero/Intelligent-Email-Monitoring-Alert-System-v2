using System.Diagnostics;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Common.Providers;
using Iemas.Application.EmailIntake.Dtos;
using Iemas.Domain.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iemas.Application.EmailIntake;

/// <summary>
/// Requirements §20 — the Email Monitoring Pipeline, Fetch through Persist stages (Classification/
/// Case stages are Phase 4/5 and are intentionally not touched here). §22 duplicate protection,
/// §78 idempotency, §79 durable background processing, §103 transaction ordering (validate state
/// → persist → commit; no external signal is ever the source of truth).
/// </summary>
public class EmailIntakeService
{
    private const int MaxMessagesPerRun = 200;

    private readonly IAppDbContext _db;
    private readonly ICredentialEncryptionService _encryptionService;
    private readonly IEmailProviderAdapterResolver _adapterResolver;
    private readonly ILogger<EmailIntakeService> _logger;

    public EmailIntakeService(
        IAppDbContext db,
        ICredentialEncryptionService encryptionService,
        IEmailProviderAdapterResolver adapterResolver,
        ILogger<EmailIntakeService> logger)
    {
        _db = db;
        _encryptionService = encryptionService;
        _adapterResolver = adapterResolver;
        _logger = logger;
    }

    /// <summary>
    /// Runs one intake cycle for every active, monitoring-enabled Inbound account. Each account
    /// is processed independently — a provider failure on one account must never prevent the
    /// others from being polled (§81 reliability: "No lost Cases", and no single-account outage
    /// should degrade the whole system).
    /// </summary>
    public async Task<List<IntakeRunResult>> RunAllAsync(CancellationToken cancellationToken)
    {
        var accountIds = await _db.EmailAccounts
            .Where(a => a.Purpose == EmailAccountPurpose.Inbound && a.IsActive && a.MonitoringEnabled)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);

        var results = new List<IntakeRunResult>();
        foreach (var accountId in accountIds)
        {
            // Re-check current state before acting (§103, §20 "Scheduled jobs must re-check
            // current state") — an admin may have disabled the account between the query above
            // and this iteration running, especially under Hangfire's own retry/scheduling.
            results.Add(await RunForAccountAsync(accountId, cancellationToken));
        }

        return results;
    }

    public async Task<IntakeRunResult> RunForAccountAsync(Guid emailAccountId, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        var account = await _db.EmailAccounts
            .Include(a => a.Credential)
            .FirstOrDefaultAsync(a => a.Id == emailAccountId, cancellationToken);

        if (account is null || !account.IsActive || !account.MonitoringEnabled || account.Purpose != EmailAccountPurpose.Inbound)
        {
            // Not an error — the account may have been deactivated/reconfigured since this run
            // was scheduled. Cancel quietly rather than treating it as a failure (§55 pattern).
            return new IntakeRunResult(emailAccountId, true, 0, 0, 0, 0, null, stopwatch.ElapsedMilliseconds);
        }

        if (account.Credential is null)
        {
            return await RecordAccountFailureAsync(account.Id, "No credential is configured for this account.", stopwatch, cancellationToken);
        }

        var syncState = await _db.EmailSyncStates.FirstOrDefaultAsync(s => s.EmailAccountId == account.Id, cancellationToken);
        if (syncState is null)
        {
            syncState = new EmailSyncState { EmailAccountId = account.Id };
            _db.EmailSyncStates.Add(syncState);
            await _db.SaveChangesAsync(cancellationToken);
        }

        syncState.LastSyncStartedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        string secret;
        try
        {
            secret = _encryptionService.Decrypt(new(
                account.Credential.EncryptedSecret,
                account.Credential.Nonce,
                account.Credential.Tag,
                account.Credential.KeyId));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return await RecordAccountFailureAsync(account.Id, "Stored credential could not be decrypted; it may be corrupted or encrypted with a rotated key.", stopwatch, cancellationToken, syncState);
        }

        var settings = new EmailProviderConnectionSettings(
            account.Protocol, account.Host, account.Port, account.Encryption, account.Username, account.AuthMethod, secret);

        FetchInboxResult fetchResult;
        try
        {
            var adapter = _adapterResolver.Resolve(account.Protocol);
            fetchResult = await adapter.FetchInboxMessagesAsync(
                settings, syncState.LastUidValidity, syncState.LastSeenUid, MaxMessagesPerRun, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Requirements: cooperative cancellation must not be recorded as a failure — the
            // caller (e.g. shutdown, or an admin pausing processing) explicitly requested this.
            throw;
        }
        catch (Exception ex)
        {
            // Provider/network/auth/TLS failure. Requirements §44: mailbox unavailability must
            // never be treated as "no messages" — the sync watermark is left untouched so the
            // next run retries from the same position instead of silently skipping messages.
            return await RecordAccountFailureAsync(account.Id, ex.Message, stopwatch, cancellationToken, syncState);
        }

        int persisted = 0, duplicates = 0;

        foreach (var message in fetchResult.Messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await PersistOneMessageAsync(account.Id, account.Protocol, message, cancellationToken);
            if (outcome == PersistOutcome.Persisted) persisted++;
            else if (outcome == PersistOutcome.Duplicate) duplicates++;
        }

        foreach (var (uid, error) in fetchResult.MalformedMessages)
        {
            _db.EmailIntakeLogs.Add(new EmailIntakeLog
            {
                EmailAccountId = account.Id,
                ProviderMessageId = uid.ToString(),
                Outcome = EmailIntakeOutcome.Skipped_Malformed,
                Detail = Truncate(error, 2000),
            });
        }

        syncState.LastUidValidity = fetchResult.UidValidity;
        syncState.LastSeenUid = fetchResult.HighestUidSeen ?? syncState.LastSeenUid;
        syncState.LastSyncCompletedAt = DateTimeOffset.UtcNow;
        syncState.LastSyncError = null;
        syncState.ConsecutiveFailureCount = 0;

        await _db.SaveChangesAsync(cancellationToken);

        stopwatch.Stop();
        _logger.LogInformation(
            "IMAP sync for account {EmailAccountId} completed in {ElapsedMilliseconds}ms: {FetchedCount} fetched, {PersistedCount} persisted, {DuplicateCount} duplicates, {MalformedCount} malformed",
            account.Id, stopwatch.ElapsedMilliseconds, fetchResult.Messages.Count, persisted, duplicates, fetchResult.MalformedMessages.Count);
        return new IntakeRunResult(
            account.Id, true, fetchResult.Messages.Count, persisted, duplicates, fetchResult.MalformedMessages.Count, null, stopwatch.ElapsedMilliseconds);
    }

    private enum PersistOutcome { Persisted, Duplicate, Failed }

    private async Task<PersistOutcome> PersistOneMessageAsync(Guid emailAccountId, Iemas.Domain.Email.EmailProtocol protocol, ProviderMessage message, CancellationToken cancellationToken)
    {
        // In-memory duplicate check first (cheap, avoids most round-trips), but the *authoritative*
        // guard is the unique DB index on (EmailAccountId, ProviderMessageId) — §22 requires
        // idempotency to hold even under concurrent/overlapping intake runs, which an in-memory
        // check alone cannot guarantee.
        var alreadyExists = await _db.EmailMessages
            .AnyAsync(m => m.EmailAccountId == emailAccountId && m.ProviderMessageId == message.ProviderMessageId, cancellationToken);

        if (alreadyExists)
        {
            _db.EmailIntakeLogs.Add(new EmailIntakeLog
            {
                EmailAccountId = emailAccountId,
                ProviderMessageId = message.ProviderMessageId,
                Outcome = EmailIntakeOutcome.Skipped_Duplicate,
            });
            return PersistOutcome.Duplicate;
        }

        var entity = new EmailMessage
        {
            EmailAccountId = emailAccountId,
            Provider = protocol,
            ProviderMessageId = message.ProviderMessageId,
            MessageId = Truncate(message.MessageId, 998),
            ThreadId = message.ThreadId,
            InReplyTo = Truncate(message.InReplyTo, 998),
            References = message.References.Count > 0 ? string.Join(' ', message.References) : null,
            FromAddress = Truncate(message.FromAddress, 320) ?? string.Empty,
            FromDisplayName = message.FromDisplayName,
            ToAddresses = string.Join(';', message.ToAddresses),
            CcAddresses = message.CcAddresses.Count > 0 ? string.Join(';', message.CcAddresses) : null,
            Subject = message.Subject,
            BodyText = message.BodyText,
            BodyHtml = message.BodyHtml,
            ReceivedAt = message.ReceivedAt,
            AttachmentCount = message.Attachments.Count,
            ProcessingStatus = EmailProcessingStatus.PendingClassification,
        };

        foreach (var attachment in message.Attachments)
        {
            entity.Attachments.Add(new EmailAttachmentMetadata
            {
                FileName = Truncate(attachment.FileName, 512) ?? "unnamed",
                ContentType = attachment.ContentType,
                SizeBytes = attachment.SizeBytes,
            });
        }

        _db.EmailMessages.Add(entity);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException saveEx)
        {
            // Requirements §22 — the unique index on (EmailAccountId, ProviderMessageId) is the
            // authoritative idempotency guard, not the in-memory AnyAsync check above (which has
            // a race window under concurrent/overlapping intake runs). Detach the failed insert,
            // then re-check the database directly: if the row now exists, a concurrent run won
            // the race and this is a genuine duplicate, not a real failure. If it still doesn't
            // exist, the save failed for some other reason and must be surfaced, not swallowed.
            _db.EmailMessages.Entry(entity).State = EntityState.Detached;

            var existsNow = await _db.EmailMessages
                .AnyAsync(m => m.EmailAccountId == emailAccountId && m.ProviderMessageId == message.ProviderMessageId, cancellationToken);

            if (!existsNow)
            {
                throw;
            }

            _db.EmailIntakeLogs.Add(new EmailIntakeLog
            {
                EmailAccountId = emailAccountId,
                ProviderMessageId = message.ProviderMessageId,
                Outcome = EmailIntakeOutcome.Skipped_Duplicate,
                Detail = $"Detected via unique constraint after save failure (concurrent intake run). Original error: {Truncate(saveEx.InnerException?.Message ?? saveEx.Message, 500)}",
            });
            await _db.SaveChangesAsync(cancellationToken);
            return PersistOutcome.Duplicate;
        }

        _db.EmailIntakeLogs.Add(new EmailIntakeLog
        {
            EmailAccountId = emailAccountId,
            ProviderMessageId = message.ProviderMessageId,
            Outcome = EmailIntakeOutcome.Fetched,
        });
        await _db.SaveChangesAsync(cancellationToken);

        return PersistOutcome.Persisted;
    }

    private async Task<IntakeRunResult> RecordAccountFailureAsync(
        Guid emailAccountId, string error, Stopwatch stopwatch, CancellationToken cancellationToken, EmailSyncState? syncState = null)
    {
        syncState ??= await _db.EmailSyncStates.FirstOrDefaultAsync(s => s.EmailAccountId == emailAccountId, cancellationToken);
        if (syncState is not null)
        {
            syncState.LastSyncError = Truncate(error, 2000);
            syncState.ConsecutiveFailureCount++;
            await _db.SaveChangesAsync(cancellationToken);
        }

        _db.EmailIntakeLogs.Add(new EmailIntakeLog
        {
            EmailAccountId = emailAccountId,
            Outcome = EmailIntakeOutcome.Failed,
            Detail = Truncate(error, 2000),
            DurationMs = stopwatch.ElapsedMilliseconds,
        });
        await _db.SaveChangesAsync(cancellationToken);

        // Never logs the account's credential/secret — only the account ID and the adapter's own
        // error message (already vetted not to contain the secret; see ImapEmailProviderAdapter).
        _logger.LogWarning(
            "IMAP sync for account {EmailAccountId} failed after {ElapsedMilliseconds}ms: {Error}",
            emailAccountId, stopwatch.ElapsedMilliseconds, error);

        stopwatch.Stop();
        return new IntakeRunResult(emailAccountId, false, 0, 0, 0, 0, error, stopwatch.ElapsedMilliseconds);
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (value is null) return null;
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
