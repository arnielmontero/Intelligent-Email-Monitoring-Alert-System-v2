using Iemas.Application.Common.Interfaces;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Cases;

public record CaseMatchResult(Case? ExistingCase, CaseMatchSignal Signal, string? Detail);

/// <summary>
/// Requirements §34 — Case Matching priority list, implemented in strict order. §19 hard
/// requirement: "IEMAS shall never match a Case based solely on subject" — normalized subject
/// (<see cref="TryMatchByNormalizedSubject"/>) is deliberately the last, weakest signal tried, and
/// even then only within the same email account + customer address pair (§37: same customer does
/// not automatically mean same Case; this narrows subject-matching to reduce false positives
/// rather than trusting it standalone across a whole mailbox).
///
/// Kept as its own class, independent of Case creation/workflow, so the matching *decision* can be
/// unit-tested in isolation from persistence — mirrors the Phase 4 pattern of
/// DeterministicEmailFilter/ClassificationDecisionPolicy being separate, narrowly-testable units.
/// </summary>
public class CaseMatchingService
{
    private readonly IAppDbContext _db;

    public CaseMatchingService(IAppDbContext db)
    {
        _db = db;
    }

    public async Task<CaseMatchResult> FindMatchingCaseAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        // 1. Provider conversation/thread ID — the strongest signal, when the provider supplies one.
        if (!string.IsNullOrWhiteSpace(message.ThreadId))
        {
            var byThread = await _db.CaseEmails
                .Include(ce => ce.EmailMessage)
                .Include(ce => ce.Case)
                .Where(ce => ce.EmailMessage.ThreadId == message.ThreadId && ce.EmailMessage.EmailAccountId == message.EmailAccountId)
                .Select(ce => ce.Case)
                .FirstOrDefaultAsync(cancellationToken);

            if (byThread is not null)
            {
                return new CaseMatchResult(byThread, CaseMatchSignal.ThreadId, $"ThreadId={message.ThreadId}");
            }
        }

        // 2. In-Reply-To — this message replies directly to a message already in a Case.
        if (!string.IsNullOrWhiteSpace(message.InReplyTo))
        {
            var byInReplyTo = await _db.CaseEmails
                .Include(ce => ce.EmailMessage)
                .Include(ce => ce.Case)
                .Where(ce => ce.EmailMessage.MessageId == message.InReplyTo && ce.EmailMessage.EmailAccountId == message.EmailAccountId)
                .Select(ce => ce.Case)
                .FirstOrDefaultAsync(cancellationToken);

            if (byInReplyTo is not null)
            {
                return new CaseMatchResult(byInReplyTo, CaseMatchSignal.InReplyTo, $"InReplyTo={message.InReplyTo}");
            }
        }

        // 3. References — any Message-ID in the References chain that belongs to an existing Case.
        var referenceIds = SplitReferences(message.References);
        if (referenceIds.Count > 0)
        {
            var byReferences = await _db.CaseEmails
                .Include(ce => ce.EmailMessage)
                .Include(ce => ce.Case)
                .Where(ce => ce.EmailMessage.MessageId != null
                    && referenceIds.Contains(ce.EmailMessage.MessageId)
                    && ce.EmailMessage.EmailAccountId == message.EmailAccountId)
                .Select(ce => ce.Case)
                .FirstOrDefaultAsync(cancellationToken);

            if (byReferences is not null)
            {
                return new CaseMatchResult(byReferences, CaseMatchSignal.References, $"References contains a known Message-ID");
            }
        }

        // 4. Message-ID relationship — this message's own MessageId was previously referenced by
        // a message already filed under a Case (the reverse direction of #2/#3: an earlier reply
        // arrived and referenced this message before this message itself was matched).
        if (!string.IsNullOrWhiteSpace(message.MessageId))
        {
            var byReverseReference = await _db.CaseEmails
                .Include(ce => ce.EmailMessage)
                .Include(ce => ce.Case)
                .Where(ce => ce.EmailMessage.EmailAccountId == message.EmailAccountId
                    && (ce.EmailMessage.InReplyTo == message.MessageId || (ce.EmailMessage.References != null && ce.EmailMessage.References.Contains(message.MessageId))))
                .Select(ce => ce.Case)
                .FirstOrDefaultAsync(cancellationToken);

            if (byReverseReference is not null)
            {
                return new CaseMatchResult(byReverseReference, CaseMatchSignal.MessageIdRelationship, $"MessageId={message.MessageId} referenced by an existing Case email");
            }
        }

        // 5. Participant + email-account relationship — an open (not Completed/Cancelled) Case
        // for the same customer on the same monitored account. §37 — same customer does not
        // automatically mean same Case, so this only considers Cases still actively open; a
        // completed/cancelled Case never silently reabsorbs a new, possibly-unrelated message
        // here (reopening a genuinely-related completed Case is handled separately by the
        // workflow service via §36/§49, using the stronger signals above, not this one).
        var openCaseSameParticipant = await _db.Cases
            .Where(c => c.EmailAccountId == message.EmailAccountId
                && c.CustomerEmailAddress == message.FromAddress
                && c.WorkStatus != CaseWorkStatus.Completed
                && c.WorkStatus != CaseWorkStatus.Cancelled
                && c.WorkStatus != CaseWorkStatus.Expired)
            .OrderByDescending(c => c.LastActivityAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (openCaseSameParticipant is not null)
        {
            return new CaseMatchResult(openCaseSameParticipant, CaseMatchSignal.ParticipantAndAccountRelationship,
                $"Open case for {message.FromAddress} on this account");
        }

        // 6. Recent conversation context — same participant/account, but the most recent case is
        // itself very recent (within the recency window), even if not "open" in the strict
        // work-status sense above (e.g. Completed very recently). This intentionally sits below
        // signal 5 and above subject, and is still narrower than reopening on subject alone.
        var recentCase = await _db.Cases
            .Where(c => c.EmailAccountId == message.EmailAccountId && c.CustomerEmailAddress == message.FromAddress)
            .OrderByDescending(c => c.LastActivityAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (recentCase is not null && (message.ReceivedAt - recentCase.LastActivityAt) <= RecentConversationWindow)
        {
            return new CaseMatchResult(recentCase, CaseMatchSignal.RecentConversationContext,
                $"Last activity on this customer's case was {(message.ReceivedAt - recentCase.LastActivityAt).TotalHours:0.#}h ago");
        }

        // 7. Normalized subject — weak signal only, never sufficient alone (§34 hard requirement).
        // Narrowed to the same account + same customer pair specifically so it cannot match a
        // different customer's unrelated case merely because both happened to write "Price
        // Request" (§19's spirit: never let subject alone drive matching, even indirectly).
        var normalizedSubject = NormalizeSubject(message.Subject);
        if (!string.IsNullOrWhiteSpace(normalizedSubject))
        {
            // Case-insensitive on purpose — subject casing varies incidentally (mail clients,
            // typing habits) and must not affect whether this weak signal can fire at all.
            var bySubject = await _db.Cases
                .Where(c => c.EmailAccountId == message.EmailAccountId
                    && c.CustomerEmailAddress == message.FromAddress
                    && c.NormalizedSubject.ToLower() == normalizedSubject.ToLower())
                .OrderByDescending(c => c.LastActivityAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (bySubject is not null)
            {
                return new CaseMatchResult(bySubject, CaseMatchSignal.NormalizedSubjectWeakSignal, $"NormalizedSubject=\"{normalizedSubject}\"");
            }
        }

        return new CaseMatchResult(null, CaseMatchSignal.NewCase, null);
    }

    private static readonly TimeSpan RecentConversationWindow = TimeSpan.FromHours(48);

    private static List<string> SplitReferences(string? references)
    {
        if (string.IsNullOrWhiteSpace(references)) return new List<string>();
        return references.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    /// <summary>Requirements §35 — strips Re:/RE:/Fwd:/FW: prefixes (repeated, case-insensitive) and trims whitespace.</summary>
    public static string NormalizeSubject(string subject)
    {
        var result = subject.Trim();
        bool strippedSomething;
        do
        {
            strippedSomething = false;
            foreach (var prefix in SubjectPrefixes)
            {
                if (result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    result = result[prefix.Length..].TrimStart(':', ' ').Trim();
                    strippedSomething = true;
                }
            }
        } while (strippedSomething && result.Length > 0);

        return result;
    }

    private static readonly string[] SubjectPrefixes = { "Re:", "RE:", "Fwd:", "FW:", "Fw:" };
}
