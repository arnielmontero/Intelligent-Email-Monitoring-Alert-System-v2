using System.Diagnostics;
using Iemas.Application.Cases.Dtos;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Common.Providers;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Cases;

/// <summary>
/// Requirements §42 (Reply Verification Engine), §44 (mailbox unavailability handling), §89
/// (investigation trail). Consumes Cases already created by Phase 5's CaseWorkflowService — this
/// phase does not create Cases or re-evaluate importance; it only checks whether a Case's
/// customer has already been replied to, via the account's own Sent folder (§42: "checking actual
/// outgoing mailbox data").
///
/// Boundary explicitly preserved per this phase's instructions: this service records the
/// *verified fact* only. It has no concept of an employee's own claim ("I already replied") —
/// that belongs to §43's Windows Agent interaction, which is Phase 7. Nothing here is pulled
/// forward from Phase 7; there is no "employee action" input to this service at all, only Cases
/// and mailbox data.
/// </summary>
public class ReplyVerificationService
{
    private const int MaxSentMessagesPerCheck = 100;

    private readonly IAppDbContext _db;
    private readonly ICredentialEncryptionService _encryptionService;
    private readonly IEmailProviderAdapterResolver _adapterResolver;

    public ReplyVerificationService(
        IAppDbContext db,
        ICredentialEncryptionService encryptionService,
        IEmailProviderAdapterResolver adapterResolver)
    {
        _db = db;
        _encryptionService = encryptionService;
        _adapterResolver = adapterResolver;
    }

    /// <summary>
    /// Runs one verification cycle for every Case still awaiting a reply. Cases are grouped by
    /// EmailAccountId so each account's Sent folder is fetched once per run, not once per Case —
    /// the same per-account-batching spirit as EmailIntakeService, just inverted (Cases already
    /// exist; this groups them by the account whose Sent folder needs checking).
    /// </summary>
    public async Task<ReplyVerificationRunResult> RunAsync(int batchSize, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // Every open Case without a verified reply is re-checked — "no reply found" only means "not
        // yet", and a reply sent later must still be found. Least recently checked first.
        var candidateCaseIds = await OpenUnrepliedCases()
            .OrderBy(c => c.ReplyLastCheckedAt ?? DateTimeOffset.MinValue)
            .ThenBy(c => c.LastActivityAt)
            .Take(batchSize)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        int verified = 0, noReply = 0, pending = 0, failed = 0;

        // Group by account so a single account's Sent folder is fetched at most once per run,
        // even if it has multiple Cases awaiting verification.
        var casesByAccount = new Dictionary<Guid, List<Guid>>();
        foreach (var caseId in candidateCaseIds)
        {
            var accountId = await _db.Cases.Where(c => c.Id == caseId).Select(c => c.EmailAccountId).FirstAsync(cancellationToken);
            if (!casesByAccount.TryGetValue(accountId, out var list))
            {
                list = new List<Guid>();
                casesByAccount[accountId] = list;
            }
            list.Add(caseId);
        }

        foreach (var (accountId, caseIds) in casesByAccount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var accountResults = await VerifyForAccountAsync(accountId, caseIds, cancellationToken);
            foreach (var outcome in accountResults)
            {
                // null = the Case was re-checked and found no longer eligible (already resolved/
                // completed by something else since the candidate query) — a quiet skip, not a
                // recorded attempt, so it must not be tallied as any outcome at all.
                switch (outcome)
                {
                    case ReplyVerificationOutcome.VerifiedReply: verified++; break;
                    case ReplyVerificationOutcome.NoReplyFound: noReply++; break;
                    case ReplyVerificationOutcome.VerificationPending: pending++; break;
                    case ReplyVerificationOutcome.VerificationFailed: failed++; break;
                    case null: break;
                }
            }
        }

        stopwatch.Stop();
        var items = await DescribeCasesAsync(_db.Cases.Where(c => candidateCaseIds.Contains(c.Id)), cancellationToken);
        return new ReplyVerificationRunResult(candidateCaseIds.Count, verified, noReply, pending, failed, stopwatch.ElapsedMilliseconds, items);
    }

    private IQueryable<Case> OpenUnrepliedCases() =>
        _db.Cases.Where(c =>
            (c.ReplyStatus == CaseReplyStatus.AwaitingReply || c.ReplyStatus == CaseReplyStatus.VerificationPending
             || c.ReplyStatus == CaseReplyStatus.NoReplyFound || c.ReplyStatus == CaseReplyStatus.VerificationFailed)
            && c.WorkStatus != CaseWorkStatus.Completed && c.WorkStatus != CaseWorkStatus.Cancelled);

    public async Task<ReplyCheckOverviewDto> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var open = _db.Cases.AsNoTracking().Where(c => c.WorkStatus != CaseWorkStatus.Completed && c.WorkStatus != CaseWorkStatus.Cancelled);
        var counts = await open.GroupBy(c => c.ReplyStatus).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        int Count(params CaseReplyStatus[] statuses) => counts.Where(c => statuses.Contains(c.Key)).Sum(c => c.Count);

        var accounts = await _db.EmailAccounts.AsNoTracking()
            .Where(a => a.Purpose == EmailAccountPurpose.Inbound)
            .Select(a => new
            {
                a.Id, a.EmailAddress,
                OpenCases = open.Count(c => c.EmailAccountId == a.Id && c.ReplyStatus != CaseReplyStatus.NotApplicable),
                LastChecked = _db.Cases.Where(c => c.EmailAccountId == a.Id).Max(c => c.ReplyLastCheckedAt),
                Latest = _db.ReplyVerificationAttempts.Where(r => r.Case.EmailAccountId == a.Id)
                    .OrderByDescending(r => r.AttemptedAt)
                    .Select(r => new { r.Outcome, r.ErrorDetail })
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var mailboxes = accounts
            .Where(a => a.OpenCases > 0 || a.LastChecked != null)
            .Select(a => new MailboxReplyCheckDto(a.Id, a.EmailAddress, a.OpenCases, a.LastChecked,
                a.Latest is { Outcome: ReplyVerificationOutcome.VerificationFailed or ReplyVerificationOutcome.VerificationPending } latest
                    ? latest.ErrorDetail ?? "The Sent folder could not be checked."
                    : null))
            .OrderBy(m => m.Mailbox)
            .ToList();

        return new ReplyCheckOverviewDto(
            Count(CaseReplyStatus.AwaitingReply), Count(CaseReplyStatus.NoReplyFound), Count(CaseReplyStatus.Replied),
            Count(CaseReplyStatus.VerificationFailed, CaseReplyStatus.VerificationPending), mailboxes);
    }

    /// <summary>Open Cases whose reply is being tracked, optionally for one reply status.</summary>
    public Task<List<ReplyCheckCaseDto>> GetCasesAsync(CaseReplyStatus? status, int take, CancellationToken cancellationToken)
    {
        var query = _db.Cases.Where(c => c.ReplyStatus != CaseReplyStatus.NotApplicable
            && c.WorkStatus != CaseWorkStatus.Completed && c.WorkStatus != CaseWorkStatus.Cancelled);
        if (status is not null) query = query.Where(c => c.ReplyStatus == status);
        return DescribeCasesAsync(query.OrderByDescending(c => c.LastActivityAt).Take(Math.Clamp(take, 1, 500)), cancellationToken);
    }

    private async Task<List<ReplyCheckCaseDto>> DescribeCasesAsync(IQueryable<Case> cases, CancellationToken cancellationToken)
    {
        var rows = await cases.AsNoTracking()
            .Select(c => new
            {
                c.Id, c.CaseNumber, c.Subject, c.CustomerEmailAddress, c.CustomerDisplayName, Mailbox = c.EmailAccount.EmailAddress,
                Owner = c.OwnerEmployee != null ? c.OwnerEmployee.FullName : null, c.ReplyStatus, c.ReplyLastCheckedAt, c.LastActivityAt,
                Latest = _db.ReplyVerificationAttempts.Where(r => r.CaseId == c.Id).OrderByDescending(r => r.AttemptedAt)
                    .Select(r => new { r.Outcome, r.ErrorDetail, r.MatchDetail }).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return rows.Select(r => new ReplyCheckCaseDto(
            r.Id, r.CaseNumber, r.Subject, r.CustomerDisplayName ?? r.CustomerEmailAddress, r.Mailbox, r.Owner, r.ReplyStatus.ToString(),
            r.ReplyLastCheckedAt,
            r.Latest is null ? null : r.Latest.Outcome switch
            {
                ReplyVerificationOutcome.VerifiedReply => $"Reply found in Sent{(string.IsNullOrWhiteSpace(r.Latest.MatchDetail) ? "" : $" ({r.Latest.MatchDetail})")}",
                ReplyVerificationOutcome.NoReplyFound => "Sent folder checked — no reply to this customer yet",
                ReplyVerificationOutcome.VerificationFailed => $"Couldn't check: {r.Latest.ErrorDetail}",
                _ => $"Pending: {r.Latest.ErrorDetail ?? "will retry"}",
            },
            r.LastActivityAt)).ToList();
    }

    /// <summary>
    /// Fetches the account's Sent folder once, then checks every candidate Case against it.
    /// A mailbox-level failure (auth/connection/folder-not-found) applies to every Case in this
    /// batch identically — §44 requires each one to become VerificationPending/Failed, never
    /// NoReplyFound, since none of them were actually checked.
    /// </summary>
    private async Task<List<ReplyVerificationOutcome?>> VerifyForAccountAsync(Guid emailAccountId, List<Guid> caseIds, CancellationToken cancellationToken)
    {
        var account = await _db.EmailAccounts.Include(a => a.Credential).FirstOrDefaultAsync(a => a.Id == emailAccountId, cancellationToken);

        // Sample mailboxes (Generate sample data) have no server; nobody can reply from them, so they behave like an
        // unanswered real mailbox and reminders and escalation run as they would for a real person.
        if (account?.Host == Operations.SampleDataService.SampleMailboxHost)
        {
            var results = new List<ReplyVerificationOutcome?>();
            foreach (var caseId in caseIds)
                results.Add(await RecordAttemptAsync(caseId, ReplyVerificationOutcome.NoReplyFound, null, ReplyMatchSignal.NoMatch, null, null, 0, cancellationToken));
            return results;
        }

        if (account is null || account.Credential is null)
        {
            var results = new List<ReplyVerificationOutcome?>();
            foreach (var caseId in caseIds)
            {
                results.Add(await RecordAttemptAsync(caseId, ReplyVerificationOutcome.VerificationFailed, null, ReplyMatchSignal.NoMatch, null,
                    "No credential is configured for this account.", 0, cancellationToken));
            }
            return results;
        }

        string secret;
        try
        {
            secret = _encryptionService.Decrypt(new(
                account.Credential.EncryptedSecret, account.Credential.Nonce, account.Credential.Tag, account.Credential.KeyId));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            var results = new List<ReplyVerificationOutcome?>();
            foreach (var caseId in caseIds)
            {
                results.Add(await RecordAttemptAsync(caseId, ReplyVerificationOutcome.VerificationFailed, null, ReplyMatchSignal.NoMatch, null,
                    "Stored credential could not be decrypted; it may be corrupted or encrypted with a rotated key.", 0, cancellationToken));
            }
            return results;
        }

        var settings = new EmailProviderConnectionSettings(
            account.Protocol, account.Host, account.Port, account.Encryption, account.Username, account.AuthMethod, secret);

        // Fetch since the earliest FirstEmailReceivedAt among this batch's Cases — a reply can
        // never predate the email it replies to, so nothing earlier is worth reading.
        var earliestSince = await _db.Cases.Where(c => caseIds.Contains(c.Id)).MinAsync(c => c.FirstEmailReceivedAt, cancellationToken);

        var stopwatch = Stopwatch.StartNew();
        FetchSentResult fetchResult;
        try
        {
            var adapter = _adapterResolver.Resolve(account.Protocol);
            fetchResult = await adapter.FetchSentMessagesAsync(settings, earliestSince, MaxSentMessagesPerCheck, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // §44 — a provider/network exception must never be treated as "no reply"; every Case
            // in this batch goes to VerificationFailed, not NoReplyFound.
            var failResults = new List<ReplyVerificationOutcome?>();
            foreach (var caseId in caseIds)
            {
                failResults.Add(await RecordAttemptAsync(caseId, ReplyVerificationOutcome.VerificationFailed, null, ReplyMatchSignal.NoMatch, null,
                    ex.Message, stopwatch.ElapsedMilliseconds, cancellationToken));
            }
            return failResults;
        }
        stopwatch.Stop();

        if (!fetchResult.FolderAccessible)
        {
            // §44 — the mailbox was reachable enough to authenticate (or the failure is folder-
            // specific) but Sent could not be inspected. Distinct from an exception above only in
            // that this is the adapter's own clean-failure signal rather than a thrown exception;
            // both land on VerificationFailed for the same reason: never assume no reply.
            var results = new List<ReplyVerificationOutcome?>();
            foreach (var caseId in caseIds)
            {
                results.Add(await RecordAttemptAsync(caseId, ReplyVerificationOutcome.VerificationFailed, null, ReplyMatchSignal.NoMatch, null,
                    fetchResult.AccessError ?? "Sent folder was not accessible.", stopwatch.ElapsedMilliseconds, cancellationToken));
            }
            return results;
        }

        var outcomes = new List<ReplyVerificationOutcome?>();
        foreach (var caseId in caseIds)
        {
            outcomes.Add(await VerifyOneCaseAsync(caseId, fetchResult.Messages, stopwatch.ElapsedMilliseconds, cancellationToken));
        }
        return outcomes;
    }

    private async Task<ReplyVerificationOutcome?> VerifyOneCaseAsync(
        Guid caseId, IReadOnlyCollection<ProviderMessage> sentMessages, long fetchDurationMs, CancellationToken cancellationToken)
    {
        // §20 "re-check current state before acting" — the Case may have been completed/cancelled
        // or already verified by a concurrent/manual run since the candidate query above. A Case
        // that's no longer eligible is a quiet skip — no attempt is recorded and nothing is
        // tallied, exactly like EmailIntakeService/EmailClassificationService's re-check pattern.
        var targetCase = await _db.Cases.FirstOrDefaultAsync(c => c.Id == caseId, cancellationToken);
        if (targetCase is null
            || targetCase.WorkStatus is CaseWorkStatus.Completed or CaseWorkStatus.Cancelled
            || targetCase.ReplyStatus == CaseReplyStatus.Replied)
        {
            return null;
        }

        var caseInboundMessages = await _db.CaseEmails
            .Where(ce => ce.CaseId == caseId)
            .Select(ce => ce.EmailMessage)
            .ToListAsync(cancellationToken);

        var matchResult = ReplyMatchingService.FindReply(caseInboundMessages, sentMessages, targetCase.CustomerEmailAddress);

        if (matchResult.MatchedMessage is not null)
        {
            return await RecordAttemptAsync(caseId, ReplyVerificationOutcome.VerifiedReply, matchResult.MatchedMessage.MessageId,
                matchResult.Signal, matchResult.Detail, null, fetchDurationMs, cancellationToken);
        }

        return await RecordAttemptAsync(caseId, ReplyVerificationOutcome.NoReplyFound, null, ReplyMatchSignal.NoMatch, null,
            null, fetchDurationMs, cancellationToken);
    }

    /// <summary>
    /// Appends the attempt to the audit trail (§89) and updates Case.ReplyStatus as a projection
    /// of the latest outcome — never the other way around, so history always reflects reality
    /// even if a caller only reads Case.ReplyStatus.
    /// </summary>
    private async Task<ReplyVerificationOutcome> RecordAttemptAsync(
        Guid caseId, ReplyVerificationOutcome outcome, string? matchedSentMessageId, ReplyMatchSignal signal, string? matchDetail,
        string? errorDetail, long durationMs, CancellationToken cancellationToken)
    {
        var targetCase = await _db.Cases.FirstOrDefaultAsync(c => c.Id == caseId, cancellationToken);
        if (targetCase is null) return outcome;

        var newStatus = outcome switch
        {
            ReplyVerificationOutcome.VerifiedReply => CaseReplyStatus.Replied,
            ReplyVerificationOutcome.NoReplyFound => CaseReplyStatus.NoReplyFound,
            ReplyVerificationOutcome.VerificationFailed => CaseReplyStatus.VerificationFailed,
            _ => CaseReplyStatus.VerificationPending,
        };
        targetCase.ReplyLastCheckedAt = DateTimeOffset.UtcNow;

        // Cases are re-checked every run until a reply is found; an unchanged result only updates
        // the check time, so the history isn't flooded with identical "no reply yet" entries.
        if (targetCase.ReplyStatus == newStatus)
        {
            await _db.SaveChangesAsync(cancellationToken);
            return outcome;
        }

        _db.ReplyVerificationAttempts.Add(new ReplyVerificationAttempt
        {
            CaseId = caseId,
            Outcome = outcome,
            MatchedSentMessageId = matchedSentMessageId,
            MatchSignal = signal,
            MatchDetail = matchDetail,
            ErrorDetail = errorDetail,
            DurationMs = durationMs,
        });

        targetCase.ReplyStatus = newStatus;
        targetCase.UpdatedAt = DateTimeOffset.UtcNow;

        // §45 — a verified reply moves the Case to IN_PROGRESS, not COMPLETED. The reply resolves
        // the *reply* requirement only; the underlying business work may still be open (§45
        // example: "Thank you, we will prepare the quotation" — reply verified, work continues).
        if (outcome == ReplyVerificationOutcome.VerifiedReply && targetCase.WorkStatus == CaseWorkStatus.ActionRequired)
        {
            targetCase.WorkStatus = CaseWorkStatus.InProgress;
        }

        var detail = outcome switch
        {
            ReplyVerificationOutcome.VerifiedReply => $"Verified reply found (matched via {signal}: {matchDetail}).",
            ReplyVerificationOutcome.NoReplyFound => "Sent folder checked; no matching reply found.",
            ReplyVerificationOutcome.VerificationFailed => $"Verification failed: {errorDetail}",
            _ => "Verification pending.",
        };

        _db.CaseEvents.Add(new CaseEvent
        {
            CaseId = caseId,
            EventType = CaseEventType.ReplyVerification,
            Detail = detail,
        });

        await _db.SaveChangesAsync(cancellationToken);
        return outcome;
    }
}
