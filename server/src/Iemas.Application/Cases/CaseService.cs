using Iemas.Application.Cases.Dtos;
using Iemas.Application.Common;
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

        var emailRows = await _db.CaseEmails.AsNoTracking()
            .Where(ce => ce.CaseId == id)
            .OrderBy(ce => ce.EmailMessage.ReceivedAt)
            .Select(ce => new
            {
                ce.EmailMessageId, ce.EmailMessage.Subject, ce.EmailMessage.FromAddress, ce.EmailMessage.ReceivedAt, ce.MatchSignal, ce.MatchDetail,
                ce.EmailMessage.FromDisplayName, ce.EmailMessage.ToAddresses, ce.EmailMessage.CcAddresses,
                ce.EmailMessage.BodyText, ce.EmailMessage.BodyHtml, ce.EmailMessage.AttachmentCount,
            })
            .ToListAsync(cancellationToken);

        var messageIds = emailRows.Select(e => e.EmailMessageId).ToList();
        var classifications = await _db.EmailClassifications.AsNoTracking()
            .Where(c => messageIds.Contains(c.EmailMessageId))
            .ToListAsync(cancellationToken);

        var emails = emailRows.Select(e =>
        {
            var c = classifications.FirstOrDefault(x => x.EmailMessageId == e.EmailMessageId);
            var fromHtml = string.IsNullOrWhiteSpace(e.BodyText) && !string.IsNullOrWhiteSpace(e.BodyHtml);
            return new CaseEmailDto(
                e.EmailMessageId, e.Subject, e.FromAddress, e.ReceivedAt, e.MatchSignal, e.MatchDetail,
                e.FromDisplayName, e.ToAddresses, e.CcAddresses,
                fromHtml ? HtmlToText(e.BodyHtml!) : e.BodyText, fromHtml, e.AttachmentCount,
                c is null ? null : new CaseEmailClassificationDto(
                    c.Decision.ToString(), c.Category, c.Priority?.ToString(), c.AiConfidence, c.Summary, c.ActionRequired, c.AiModel,
                    c.Legitimate, c.ResponseExpected, c.DecisionReason));
        }).ToList();

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

        var notifications = await _db.Notifications.AsNoTracking()
            .Where(n => n.CaseId == id)
            .OrderBy(n => n.CreatedAt)
            .Select(n => new { n.Id, n.CreatedAt, n.Type, n.Status, n.Title, n.Message, EmployeeName = n.Employee.FullName, n.DeliveredAgentCount, n.AcknowledgedAt })
            .ToListAsync(cancellationToken);

        return new CaseDetailDto(caseDto, emails, history, verificationAttempts,
            notifications.Select(n => new CaseNotificationDto(n.Id, n.CreatedAt, n.Type.ToString(), n.Status.ToString(), n.Title, n.Message,
                n.EmployeeName, n.DeliveredAgentCount, n.AcknowledgedAt)).ToList());
    }

    /// <summary>
    /// Requirements §66/§67/§86/§89 — "Case History & Logs" as a global, cross-Case searchable log
    /// (distinct from GetDetailAsync's per-Case timeline, and distinct from the System Audit Log,
    /// which tracks admin/config changes rather than "what happened to a Case"). Read-only —
    /// CaseEvent is append-only by design (§66); nothing here ever writes a row.
    /// </summary>
    public async Task<PagedResult<CaseEventSearchResultDto>> SearchEventsAsync(
        CaseEventSearchFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);
        page = Math.Max(page, 1);

        var query = _db.CaseEvents.AsNoTracking().AsQueryable();

        if (filter.CaseId is not null) query = query.Where(e => e.CaseId == filter.CaseId);
        if (filter.EventType is not null) query = query.Where(e => e.EventType == filter.EventType);
        if (filter.EmailAccountId is not null) query = query.Where(e => e.Case.EmailAccountId == filter.EmailAccountId);
        if (filter.OwnerEmployeeId is not null) query = query.Where(e => e.Case.OwnerEmployeeId == filter.OwnerEmployeeId);
        if (filter.From is not null) query = query.Where(e => e.OccurredAt >= filter.From);
        if (filter.To is not null) query = query.Where(e => e.OccurredAt <= filter.To);

        // One box searches everything a person would type: case number, subject, customer, mailbox, owner, event text.
        var term = filter.Search?.Trim().ToLower();
        if (!string.IsNullOrEmpty(term))
        {
            query = query.Where(e =>
                e.Case.CaseNumber.ToLower().Contains(term)
                || e.Case.Subject.ToLower().Contains(term)
                || e.Case.CustomerEmailAddress.ToLower().Contains(term)
                || (e.Case.CustomerDisplayName != null && e.Case.CustomerDisplayName.ToLower().Contains(term))
                || e.Case.EmailAccount.EmailAddress.ToLower().Contains(term)
                || (e.Case.OwnerEmployee != null && e.Case.OwnerEmployee.FullName.ToLower().Contains(term))
                || e.Detail.ToLower().Contains(term));
        }

        var total = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        page = Math.Min(page, totalPages);

        var events = await query
            .OrderByDescending(e => e.OccurredAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new
            {
                e.Id, e.CaseId, e.Case.CaseNumber, e.Case.Subject, e.EventType, e.Detail, e.ActorEmployeeId, e.OccurredAt,
                e.Case.CustomerEmailAddress, e.Case.CustomerDisplayName,
                Mailbox = e.Case.EmailAccount.EmailAddress,
                OwnerName = e.Case.OwnerEmployee != null ? e.Case.OwnerEmployee.FullName : null,
            })
            .ToListAsync(cancellationToken);

        var actorIds = events.Where(e => e.ActorEmployeeId != null).Select(e => e.ActorEmployeeId!.Value).Distinct().ToList();
        var actorNames = await _db.Employees.Where(emp => actorIds.Contains(emp.Id)).ToDictionaryAsync(emp => emp.Id, emp => emp.FullName, cancellationToken);

        var items = events
            .Select(e => new CaseEventSearchResultDto(
                e.Id, e.CaseId, e.CaseNumber, e.Subject, e.EventType, e.Detail,
                e.ActorEmployeeId, e.ActorEmployeeId != null ? actorNames.GetValueOrDefault(e.ActorEmployeeId.Value) : null, e.OccurredAt,
                e.CustomerEmailAddress, e.CustomerDisplayName, e.Mailbox, e.OwnerName))
            .ToList();

        return PagedResult<CaseEventSearchResultDto>.Create(items, total, page, pageSize);
    }

    /// <summary>Plain-text rendering of an HTML-only email for display; the CMS never renders email HTML.</summary>
    private static string HtmlToText(string html)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(html, @"<(script|style)[^>]*>[\s\S]*?</\1>", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<(br|/p|/div|/tr|/li|/h[1-6])[^>]*>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]+", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*\n\s*", "\n");
        return text.Trim();
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
