using Iemas.Application.Common.Interfaces;
using Iemas.Domain.Cases;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Inquiries;

/// <summary>
/// Module 7 / G4 - finds the inquiry a message belongs to using only strong thread signals
/// (provider thread id, In-Reply-To, References) on the same mailbox. Used before screening and
/// classification so a follow-up in an existing conversation ("any update?", "thanks, but it still
/// fails", an internal forward) always stays with its inquiry instead of depending on the AI calling
/// it important. Never uses subject or sender alone.
/// </summary>
public static class ConversationThreadMatcher
{
    public static async Task<Case?> FindCaseAsync(
        IAppDbContext db, Guid emailAccountId, string? threadId, string? inReplyTo, string? references, CancellationToken cancellationToken)
    {
        var linked = db.CaseEmails.Where(ce => ce.EmailMessage.EmailAccountId == emailAccountId);

        if (!string.IsNullOrWhiteSpace(threadId))
        {
            var byThread = await linked.Where(ce => ce.EmailMessage.ThreadId == threadId)
                .OrderByDescending(ce => ce.Case.LastActivityAt).Select(ce => ce.Case).FirstOrDefaultAsync(cancellationToken);
            if (byThread is not null) return byThread;
        }

        var ids = new List<string>();
        if (!string.IsNullOrWhiteSpace(inReplyTo)) ids.Add(inReplyTo.Trim());
        ids.AddRange((references ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (ids.Count == 0) return null;

        return await linked.Where(ce => ce.EmailMessage.MessageId != null && ids.Contains(ce.EmailMessage.MessageId))
            .OrderByDescending(ce => ce.Case.LastActivityAt).Select(ce => ce.Case).FirstOrDefaultAsync(cancellationToken);
    }
}
