using Iemas.Application.Common.Providers;
using Iemas.Domain.Cases;

namespace Iemas.Application.Cases;

public record ReplyMatchResult(ProviderMessage? MatchedMessage, ReplyMatchSignal Signal, string? Detail);

/// <summary>
/// Requirements §42 — matches a Sent message back to a specific Case using the same
/// strength-ordered identifier approach as §34 Case Matching (<see cref="CaseMatchingService"/>),
/// applied in the reverse direction: given the Case's already-linked inbound
/// <see cref="Iemas.Domain.Email.EmailMessage"/> rows, does any Sent message reply to one of them?
///
/// Deliberately stricter than Case Matching: subject is not a signal here at all (see
/// <see cref="ReplyMatchSignal"/> doc) — a *verified* reply requires a real identifier
/// relationship, never a subject-only coincidence. This is the specific hard requirement this
/// phase adds on top of §34's existing "subject alone must never determine a match" rule.
///
/// Pure/stateless — takes the Case's inbound messages and the fetched Sent messages as plain
/// input, no DB access itself, so it can be unit-tested in isolation exactly like
/// CaseMatchingService's NormalizeSubject/priority-order tests.
/// </summary>
public static class ReplyMatchingService
{
    public static ReplyMatchResult FindReply(
        IReadOnlyCollection<Iemas.Domain.Email.EmailMessage> caseInboundMessages,
        IReadOnlyCollection<ProviderMessage> sentMessages,
        string customerEmailAddress)
    {
        var inboundThreadIds = caseInboundMessages.Select(m => m.ThreadId).Where(t => !string.IsNullOrWhiteSpace(t)).ToHashSet();
        var inboundMessageIds = caseInboundMessages.Select(m => m.MessageId).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet();

        // 1. Thread ID — the Sent message is part of the same provider conversation thread.
        var byThread = sentMessages.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.ThreadId) && inboundThreadIds.Contains(s.ThreadId));
        if (byThread is not null)
        {
            return new ReplyMatchResult(byThread, ReplyMatchSignal.ThreadId, $"ThreadId={byThread.ThreadId}");
        }

        // 2. In-Reply-To — the Sent message directly replies to one of the Case's inbound messages.
        var byInReplyTo = sentMessages.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.InReplyTo) && inboundMessageIds.Contains(s.InReplyTo));
        if (byInReplyTo is not null)
        {
            return new ReplyMatchResult(byInReplyTo, ReplyMatchSignal.InReplyTo, $"InReplyTo={byInReplyTo.InReplyTo}");
        }

        // 3. References — one of the Case's inbound Message-IDs appears anywhere in the Sent
        // message's References chain (a reply further down a long thread still carries every
        // ancestor Message-ID here, not just the immediate parent).
        var byReferences = sentMessages.FirstOrDefault(s => s.References.Any(r => inboundMessageIds.Contains(r)));
        if (byReferences is not null)
        {
            return new ReplyMatchResult(byReferences, ReplyMatchSignal.References, "References contains a known inbound Message-ID");
        }

        // 4. Message-ID relationship — the reverse direction: one of the Case's inbound messages
        // itself references/replies-to a Sent message's Message-ID (covers an inbound follow-up
        // arriving between the reply being sent and this verification pass running, where the
        // *inbound* side recorded the relationship rather than the outbound side being checked
        // directly above).
        var inboundReferenceIds = caseInboundMessages
            .SelectMany(m => (m.References ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Concat(caseInboundMessages.Where(m => m.InReplyTo != null).Select(m => m.InReplyTo!))
            .ToHashSet();
        var byReverseReference = sentMessages.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.MessageId) && inboundReferenceIds.Contains(s.MessageId));
        if (byReverseReference is not null)
        {
            return new ReplyMatchResult(byReverseReference, ReplyMatchSignal.MessageIdRelationship, $"MessageId={byReverseReference.MessageId} referenced by an inbound Case email");
        }

        // 5. Recipient + account relationship — a Sent message addressed to the Case's customer,
        // with no thread signal at all (some providers/clients don't populate References/
        // In-Reply-To reliably). Weakest signal this phase supports; still an actual identifier
        // relationship (the To address), never subject text.
        var byRecipient = sentMessages.FirstOrDefault(s => s.ToAddresses.Any(to => string.Equals(to, customerEmailAddress, StringComparison.OrdinalIgnoreCase)));
        if (byRecipient is not null)
        {
            return new ReplyMatchResult(byRecipient, ReplyMatchSignal.RecipientAndAccountRelationship, $"Sent to {customerEmailAddress}");
        }

        return new ReplyMatchResult(null, ReplyMatchSignal.NoMatch, null);
    }
}
