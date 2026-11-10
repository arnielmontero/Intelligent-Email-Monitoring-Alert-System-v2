using Iemas.Application.Common.Interfaces;
using Iemas.Application.Inquiries;
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

        // 5. A NEW email thread from a customer who already has an open inquiry on this account.
        // Nothing above links it to a conversation, so by default it is a new inquiry and gets its own
        // Case. It only joins an existing open Case when that is clearly the same matter:
        //   a) same subject (the customer re-sent or nudged the same question), or
        //   b) we have not answered that Case yet (the customer is chasing us or adding details; the
        //      agent gets a "still needs your response" alert instead of a second Case for the same wait).
        // Once we HAVE replied and the new email is about something else, it is a separate inquiry.
        //
        // A closed Case (completed, cancelled, expired, resolved) is never reopened by a new thread: only a
        // real reply in its own conversation (signals 1-4 above) reopens it. The earlier "recent
        // conversation" and "subject only" fallbacks were removed for that reason: they let an unrelated
        // new request silently reopen an old, finished Case (§37: same customer is not the same Case).
        var normalizedSubject = NormalizeSubject(message.Subject);
        var openCases = await _db.Cases
            .Where(c => c.EmailAccountId == message.EmailAccountId
                && c.CustomerEmailAddress == message.FromAddress
                && c.WorkStatus != CaseWorkStatus.Completed
                && c.WorkStatus != CaseWorkStatus.Cancelled
                && c.WorkStatus != CaseWorkStatus.Expired
                && c.Stage != InquiryStage.Resolved
                && c.Stage != InquiryStage.Closed)
            .OrderByDescending(c => c.LastActivityAt)
            .ToListAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(normalizedSubject))
        {
            // Case-insensitive on purpose: subject casing varies incidentally between mail clients.
            var sameSubject = openCases.FirstOrDefault(c => string.Equals(c.NormalizedSubject, normalizedSubject, StringComparison.OrdinalIgnoreCase));
            if (sameSubject is not null)
            {
                return new CaseMatchResult(sameSubject, CaseMatchSignal.NormalizedSubjectWeakSignal,
                    $"Same subject \"{normalizedSubject}\" as an open Case of this customer");
            }
        }

        var stillWaitingForUs = openCases.FirstOrDefault(c => InquiryLifecycle.AgentOwesNextStep(c.Stage));
        if (stillWaitingForUs is not null)
        {
            return new CaseMatchResult(stillWaitingForUs, CaseMatchSignal.ParticipantAndAccountRelationship,
                $"Open Case of {message.FromAddress} that has not been answered yet");
        }

        return new CaseMatchResult(null, CaseMatchSignal.NewCase, null);
    }

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
