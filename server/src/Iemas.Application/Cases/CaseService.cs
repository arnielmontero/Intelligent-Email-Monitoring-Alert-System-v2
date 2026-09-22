using Iemas.Application.Cases.Dtos;
using Iemas.Application.Common.Interfaces;
using Iemas.Domain.Cases;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Cases;

/// <summary>Requirements §88 (Search/Filters), §89 (Case Investigation timeline).</summary>
public class CaseService
{
    private readonly IAppDbContext _db;

    public CaseService(IAppDbContext db)
    {
        _db = db;
    }

    public async Task<List<CaseDto>> SearchAsync(CaseListFilter filter, CancellationToken cancellationToken)
    {
        var query = _db.Cases.AsNoTracking().AsQueryable();

        if (filter.WorkStatus is not null) query = query.Where(c => c.WorkStatus == filter.WorkStatus);
        if (filter.OwnerEmployeeId is not null) query = query.Where(c => c.OwnerEmployeeId == filter.OwnerEmployeeId);
        if (filter.EmailAccountId is not null) query = query.Where(c => c.EmailAccountId == filter.EmailAccountId);
        if (!string.IsNullOrWhiteSpace(filter.CustomerEmailAddress)) query = query.Where(c => c.CustomerEmailAddress == filter.CustomerEmailAddress);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            query = query.Where(c => c.CaseNumber.Contains(term) || c.Subject.Contains(term) || c.CustomerEmailAddress.Contains(term));
        }

        return await ProjectAndOrder(query).ToListAsync(cancellationToken);
    }

    public async Task<CaseDetailDto?> GetDetailAsync(Guid id, CancellationToken cancellationToken)
    {
        var caseDto = await ProjectAndOrder(_db.Cases.AsNoTracking().Where(c => c.Id == id)).FirstOrDefaultAsync(cancellationToken);
        if (caseDto is null) return null;

        var emails = await _db.CaseEmails.AsNoTracking()
            .Where(ce => ce.CaseId == id)
            .OrderBy(ce => ce.EmailMessage.ReceivedAt)
            .Select(ce => new CaseEmailDto(ce.EmailMessageId, ce.EmailMessage.Subject, ce.EmailMessage.FromAddress, ce.EmailMessage.ReceivedAt, ce.MatchSignal, ce.MatchDetail))
            .ToListAsync(cancellationToken);

        // Requirements §66/§89 — append-only, chronological investigation timeline.
        var history = await _db.CaseEvents.AsNoTracking()
            .Where(e => e.CaseId == id)
            .OrderBy(e => e.OccurredAt)
            .Select(e => new CaseEventDto(e.Id, e.EventType, e.Detail, e.ActorEmployeeId, e.OccurredAt))
            .ToListAsync(cancellationToken);

        // Requirements §42/§89 — the full reply-verification attempt trail, not just the summary
        // that already appears in the CaseEvent history above.
        var verificationAttempts = await _db.ReplyVerificationAttempts.AsNoTracking()
            .Where(a => a.CaseId == id)
            .OrderBy(a => a.AttemptedAt)
            .Select(a => new ReplyVerificationAttemptDto(a.Id, a.Outcome, a.MatchedSentMessageId, a.MatchSignal, a.MatchDetail, a.ErrorDetail, a.AttemptedAt, a.DurationMs))
            .ToListAsync(cancellationToken);

        return new CaseDetailDto(caseDto, emails, history, verificationAttempts);
    }

    private static IQueryable<CaseDto> ProjectAndOrder(IQueryable<Case> source)
    {
        // Order before projecting — see EmailAccountService for why (EF Core can't translate
        // ORDER BY applied after a record-constructing Select; live-verified in Phase 2).
        return source
            .OrderByDescending(c => c.LastActivityAt)
            .Select(c => new CaseDto(
                c.Id, c.CaseNumber, c.EmailAccountId, c.EmailAccount.EmailAddress,
                c.CustomerEmailAddress, c.CustomerDisplayName,
                c.OwnerEmployeeId, c.OwnerEmployee != null ? c.OwnerEmployee.FullName : null,
                c.Subject, c.WorkStatus, c.ReplyStatus, c.NotificationStatus,
                c.FirstEmailReceivedAt, c.LastActivityAt,
                c.CompletionReason, c.CompletionComment, c.CompletedAt, c.ReopenCount,
                c.Emails.Count));
    }
}
