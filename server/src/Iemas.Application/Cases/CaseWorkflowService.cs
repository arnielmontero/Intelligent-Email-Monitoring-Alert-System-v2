using System.Diagnostics;
using Iemas.Application.Cases.Dtos;
using Iemas.Application.Common;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Notifications;
using Iemas.Application.Operations;
using Iemas.Application.Reminders;
using Iemas.Domain.Ai;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Iemas.Domain.Notifications;
using Iemas.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Cases;

public record CaseRunResult(int ConsideredCount, int CreatedCount, int UpdatedCount, int ReopenedCount, long DurationMs, List<CaseWorkflowItemDto>? Items = null);

/// <summary>Current number of emails/Cases at each stage of the workflow.</summary>
public record CaseWorkflowOverviewDto(
    int ReceivedLast24Hours,
    int AwaitingAiCheck,
    int NeedsReview,
    int NotWorkLast24Hours,
    int WaitingForCase,
    int ActionRequired,
    int InProgress,
    int WaitingOnCustomer,
    int WaitingInternally,
    int Overdue,
    int Escalated,
    int CompletedLast7Days);

/// <summary>One email and what the Case step did with it (or will do, for "Waiting").</summary>
public record CaseWorkflowItemDto(
    Guid EmailMessageId,
    DateTimeOffset ReceivedAt,
    string FromAddress,
    string Subject,
    string Mailbox,
    string Outcome,
    Guid? CaseId,
    string? CaseNumber,
    string? Detail,
    DateTimeOffset? ProcessedAt = null);

/// <summary>
/// Requirements §20 (pipeline: Case Matching/Creation stage, following Phase 4's Classification
/// stage), §32-§41 (Case model, creation, matching, status, reopening), §66 (Case History), Core
/// Principle 9 boundary reaffirmed for this phase: this service consumes the already-persisted
/// <see cref="ImportanceDecision"/> from Phase 4 — it never re-derives relevance or re-runs AI, it
/// only decides Case placement for a decision that was already made.
///
/// §33 — non-relevant email is still stored/classified but never turns into a Case: this service
/// only considers <see cref="EmailProcessingStatus.Processed"/> messages whose classification
/// <see cref="ImportanceDecision"/> is Important. ReviewRequired messages are deliberately left
/// alone here (no Case yet) — Case creation for ReviewRequired is a business decision explicitly
/// not frozen by the requirements; recorded in the tracker rather than silently assumed either way.
/// </summary>
public class CaseWorkflowService
{
    private readonly IAppDbContext _db;
    private readonly CaseMatchingService _matchingService;
    private readonly ReminderSchedulingService _reminderSchedulingService;
    private readonly NotificationService? _notificationService;

    public CaseWorkflowService(
        IAppDbContext db,
        CaseMatchingService matchingService,
        ReminderSchedulingService reminderSchedulingService,
        NotificationService? notificationService = null)
    {
        _db = db;
        _matchingService = matchingService;
        _reminderSchedulingService = reminderSchedulingService;
        _notificationService = notificationService;
    }

    public async Task<CaseRunResult> RunAsync(int batchSize, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // §91 Pause Email Processing — classified messages simply wait; nothing is lost or deleted.
        if (await _db.IsPausedAsync(PauseControl.EmailProcessing, cancellationToken))
        {
            return new CaseRunResult(0, 0, 0, 0, stopwatch.ElapsedMilliseconds);
        }

        // §20 "Scheduled jobs must re-check current state" — the candidate set is read fresh
        // each run; a message picked up by RunAsync always re-validates its own state inside
        // ProcessOneAsync before acting, exactly like EmailIntakeService/EmailClassificationService.
        var candidateIds = await CandidateMessageIds()
            .OrderBy(id => id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        int created = 0, updated = 0, reopened = 0;
        var outcomes = new Dictionary<Guid, CaseProcessOutcome>();

        foreach (var messageId in candidateIds)
        {
            var outcome = await ProcessOneAsync(messageId, cancellationToken);
            outcomes[messageId] = outcome;
            switch (outcome)
            {
                case CaseProcessOutcome.Created: created++; break;
                case CaseProcessOutcome.Updated: updated++; break;
                case CaseProcessOutcome.Reopened: reopened++; break;
            }
        }

        var items = (await DescribeAsync(candidateIds, cancellationToken))
            .Select(i => i with
            {
                Outcome = outcomes[i.EmailMessageId] switch
                {
                    CaseProcessOutcome.Created => "New Case",
                    CaseProcessOutcome.Updated => "Added to existing Case",
                    CaseProcessOutcome.Reopened => "Reopened Case",
                    _ => "Skipped",
                },
                Detail = outcomes[i.EmailMessageId] == CaseProcessOutcome.Skipped
                    ? "Not turned into a Case (already handled, older than the mailbox cut-off, or sender is ignored)."
                    : i.Detail,
            })
            .ToList();

        stopwatch.Stop();
        return new CaseRunResult(candidateIds.Count, created, updated, reopened, stopwatch.ElapsedMilliseconds, items);
    }

    public async Task<CaseWorkflowOverviewDto> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var dayAgo = now.AddDays(-1);
        var weekAgo = now.AddDays(-7);

        // By the email's own arrival time, so a mailbox catching up on old mail doesn't inflate "received".
        var received = await _db.EmailMessages.CountAsync(m => m.ReceivedAt >= dayAgo
            && m.ProcessingStatus != EmailProcessingStatus.Historical && m.ProcessingStatus != EmailProcessingStatus.Ignored, cancellationToken);
        var awaitingAi = await _db.EmailMessages.CountAsync(m => m.ProcessingStatus == EmailProcessingStatus.PendingClassification, cancellationToken);
        var needsReview = await _db.EmailMessages.CountAsync(m => m.ProcessingStatus == EmailProcessingStatus.ReviewRequired
            && (m.EmailAccount.ProcessEmailsReceivedAfter == null || m.ReceivedAt >= m.EmailAccount.ProcessEmailsReceivedAfter), cancellationToken);
        var notWork = await _db.EmailClassifications.CountAsync(c => c.Decision == ImportanceDecision.NotImportant && c.ClassifiedAt >= dayAgo, cancellationToken);
        var waitingForCase = await CandidateMessageIds().CountAsync(cancellationToken);

        var byStatus = await _db.Cases.AsNoTracking()
            .GroupBy(c => c.WorkStatus)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        int Count(params CaseWorkStatus[] statuses) => byStatus.Where(s => statuses.Contains(s.Status)).Sum(s => s.Count);
        var completed = await _db.Cases.CountAsync(c => c.WorkStatus == CaseWorkStatus.Completed && c.CompletedAt >= weekAgo, cancellationToken);

        return new CaseWorkflowOverviewDto(
            received, awaitingAi, needsReview + Count(CaseWorkStatus.ReviewRequired), notWork, waitingForCase,
            Count(CaseWorkStatus.New, CaseWorkStatus.ActionRequired), Count(CaseWorkStatus.InProgress),
            Count(CaseWorkStatus.WaitingForCustomer), Count(CaseWorkStatus.WaitingForInternal, CaseWorkStatus.WaitingForApproval),
            Count(CaseWorkStatus.Overdue), Count(CaseWorkStatus.Escalated), completed);
    }

    /// <summary>Important email waiting to be placed into a Case — what the next run will process.</summary>
    public async Task<List<CaseWorkflowItemDto>> GetWaitingAsync(int take, CancellationToken cancellationToken)
    {
        var ids = await CandidateMessageIds().Take(Math.Clamp(take, 1, 200)).ToListAsync(cancellationToken);
        var ignored = await IgnoredSenderList.LoadAsync(_db, cancellationToken);
        return (await DescribeAsync(ids, cancellationToken))
            .Where(i => !ignored.Matches(i.FromAddress))
            .Select(i => i with { Outcome = "Waiting", CaseId = null, CaseNumber = null })
            .OrderBy(i => i.ReceivedAt)
            .ToList();
    }

    /// <summary>Emails placed into Cases (by the automatic runs or Run Now), searchable, sortable and paged; newest first by default.</summary>
    public async Task<PagedResult<CaseWorkflowItemDto>> GetRecentAsync(
        int page, int pageSize, string? search, string? sort, bool descending, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = _db.CaseEmails.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(ce => ce.EmailMessage.FromAddress.ToLower().Contains(term)
                || ce.EmailMessage.Subject.ToLower().Contains(term)
                || ce.EmailMessage.EmailAccount.EmailAddress.ToLower().Contains(term)
                || ce.Case.CaseNumber.ToLower().Contains(term));
        }

        query = (sort?.ToLowerInvariant()) switch
        {
            "received" => descending ? query.OrderByDescending(ce => ce.EmailMessage.ReceivedAt) : query.OrderBy(ce => ce.EmailMessage.ReceivedAt),
            "from" => descending ? query.OrderByDescending(ce => ce.EmailMessage.FromAddress) : query.OrderBy(ce => ce.EmailMessage.FromAddress),
            "subject" => descending ? query.OrderByDescending(ce => ce.EmailMessage.Subject) : query.OrderBy(ce => ce.EmailMessage.Subject),
            "mailbox" => descending ? query.OrderByDescending(ce => ce.EmailMessage.EmailAccount.EmailAddress) : query.OrderBy(ce => ce.EmailMessage.EmailAccount.EmailAddress),
            "case" => descending ? query.OrderByDescending(ce => ce.Case.CaseNumber) : query.OrderBy(ce => ce.Case.CaseNumber),
            _ => descending ? query.OrderByDescending(ce => ce.CreatedAt) : query.OrderBy(ce => ce.CreatedAt),
        };

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(ce => new
            {
                ce.EmailMessageId, ce.EmailMessage.ReceivedAt, ce.EmailMessage.FromAddress, ce.EmailMessage.Subject,
                Mailbox = ce.EmailMessage.EmailAccount.EmailAddress, ce.CaseId, ce.Case.CaseNumber, ce.MatchSignal, ce.MatchDetail, ce.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        var items = rows.Select(r => new CaseWorkflowItemDto(
            r.EmailMessageId, r.ReceivedAt, r.FromAddress, r.Subject, r.Mailbox,
            r.MatchSignal == CaseMatchSignal.NewCase ? "New Case" : "Added to existing Case",
            r.CaseId, r.CaseNumber,
            r.MatchSignal == CaseMatchSignal.NewCase ? null : $"Matched by {DescribeSignal(r.MatchSignal)}",
            r.CreatedAt)).ToList();
        return PagedResult<CaseWorkflowItemDto>.Create(items, total, page, pageSize);
    }

    private IQueryable<Guid> CandidateMessageIds() =>
        _db.EmailClassifications
            .Where(c => c.Decision == ImportanceDecision.Important)
            .Join(_db.EmailMessages.Where(m => m.ProcessingStatus == EmailProcessingStatus.Processed
                        && (m.EmailAccount.ProcessEmailsReceivedAfter == null || m.ReceivedAt >= m.EmailAccount.ProcessEmailsReceivedAfter)),
                c => c.EmailMessageId, m => m.Id, (c, m) => m.Id)
            .Where(messageId => !_db.CaseEmails.Any(ce => ce.EmailMessageId == messageId));

    private async Task<List<CaseWorkflowItemDto>> DescribeAsync(List<Guid> messageIds, CancellationToken cancellationToken)
    {
        var messages = await _db.EmailMessages.AsNoTracking()
            .Where(m => messageIds.Contains(m.Id))
            .Select(m => new { m.Id, m.ReceivedAt, m.FromAddress, m.Subject, Mailbox = m.EmailAccount.EmailAddress })
            .ToListAsync(cancellationToken);
        var links = await _db.CaseEmails.AsNoTracking()
            .Where(ce => messageIds.Contains(ce.EmailMessageId))
            .Select(ce => new { ce.EmailMessageId, ce.CaseId, ce.Case.CaseNumber, ce.MatchSignal })
            .ToListAsync(cancellationToken);

        return messages.Select(m =>
        {
            var link = links.FirstOrDefault(l => l.EmailMessageId == m.Id);
            return new CaseWorkflowItemDto(m.Id, m.ReceivedAt, m.FromAddress, m.Subject, m.Mailbox, string.Empty,
                link?.CaseId, link?.CaseNumber,
                link is null || link.MatchSignal == CaseMatchSignal.NewCase ? null : $"Matched by {DescribeSignal(link.MatchSignal)}");
        }).ToList();
    }

    private static string DescribeSignal(CaseMatchSignal signal) => signal switch
    {
        CaseMatchSignal.ThreadId => "the same email thread",
        CaseMatchSignal.InReplyTo or CaseMatchSignal.References or CaseMatchSignal.MessageIdRelationship => "a reply to an earlier email",
        CaseMatchSignal.ParticipantAndAccountRelationship or CaseMatchSignal.RecentConversationContext => "the same customer and recent conversation",
        CaseMatchSignal.NormalizedSubjectWeakSignal => "a matching subject",
        _ => signal.ToString(),
    };

    public enum CaseProcessOutcome { Created, Updated, Reopened, Skipped }

    /// <summary>
    /// Idempotent per message (§78): a message already linked to a Case (via the unique index on
    /// <see cref="CaseEmail.EmailMessageId"/>) is never linked a second time, even under a
    /// concurrent/overlapping run — the DB unique constraint is the authoritative guard, the
    /// upfront query above is just the cheap pre-filter, same pattern as EmailIntakeService.
    /// </summary>
    public async Task<CaseProcessOutcome> ProcessOneAsync(Guid emailMessageId, CancellationToken cancellationToken)
    {
        if (await _db.CaseEmails.AnyAsync(ce => ce.EmailMessageId == emailMessageId, cancellationToken))
        {
            return CaseProcessOutcome.Skipped;
        }

        var message = await _db.EmailMessages.FirstOrDefaultAsync(m => m.Id == emailMessageId, cancellationToken);
        if (message is null || message.ProcessingStatus != EmailProcessingStatus.Processed)
        {
            return CaseProcessOutcome.Skipped;
        }

        // Email from before the mailbox's cut-off is history, even if it was classified before the cut-off was set.
        var cutoff = await _db.EmailAccounts.Where(a => a.Id == message.EmailAccountId).Select(a => a.ProcessEmailsReceivedAfter).FirstOrDefaultAsync(cancellationToken);
        if (cutoff is DateTimeOffset processAfter && message.ReceivedAt < processAfter)
        {
            return CaseProcessOutcome.Skipped;
        }

        if ((await IgnoredSenderList.LoadAsync(_db, cancellationToken)).Matches(message.FromAddress))
        {
            return CaseProcessOutcome.Skipped;
        }

        var classification = await _db.EmailClassifications.AsNoTracking()
            .FirstOrDefaultAsync(c => c.EmailMessageId == emailMessageId, cancellationToken);
        if (classification is null || classification.Decision != ImportanceDecision.Important)
        {
            return CaseProcessOutcome.Skipped;
        }

        var matchResult = await _matchingService.FindMatchingCaseAsync(message, cancellationToken);
        var outcome = CaseProcessOutcome.Updated;

        Case targetCase;
        if (matchResult.ExistingCase is null)
        {
            targetCase = await CreateCaseAsync(message, cancellationToken);
            outcome = CaseProcessOutcome.Created;
        }
        else
        {
            targetCase = matchResult.ExistingCase;

            // §36/§49 — a completed Case receiving a clearly related new email reopens; history
            // of the prior completion is preserved (append-only, never overwritten).
            if (targetCase.WorkStatus == CaseWorkStatus.Completed || targetCase.WorkStatus == CaseWorkStatus.Cancelled)
            {
                targetCase.WorkStatus = CaseWorkStatus.ActionRequired;
                targetCase.ReplyStatus = CaseReplyStatus.AwaitingReply;
                targetCase.CompletionReason = null;
                targetCase.CompletionComment = null;
                targetCase.CompletedAt = null;
                targetCase.ReopenCount++;
                targetCase.UpdatedAt = DateTimeOffset.UtcNow;

                _db.CaseEvents.Add(new CaseEvent
                {
                    CaseId = targetCase.Id,
                    EventType = CaseEventType.Reopened,
                    Detail = $"Reopened by new related email (matched via {matchResult.Signal}: {matchResult.Detail}).",
                });
                outcome = CaseProcessOutcome.Reopened;
            }
            else
            {
                // §39 — a new customer message before the employee has replied must not spin up
                // a duplicate reminder cycle; it re-evaluates the existing Case rather than
                // resetting it. WorkStatus only moves back to ActionRequired if the Case was
                // waiting specifically on the customer's own reply — any other active state
                // (e.g. WaitingForInternal, InProgress) is left as-is; a new inbound email does
                // not override internal/approval workflow the way it overrides "we were waiting
                // to hear from the customer."
                if (targetCase.WorkStatus == CaseWorkStatus.WaitingForCustomer)
                {
                    targetCase.WorkStatus = CaseWorkStatus.ActionRequired;
                }
                targetCase.ReplyStatus = CaseReplyStatus.AwaitingReply;
                targetCase.UpdatedAt = DateTimeOffset.UtcNow;
            }

            targetCase.LastActivityAt = message.ReceivedAt > targetCase.LastActivityAt ? message.ReceivedAt : targetCase.LastActivityAt;
        }

        _db.CaseEmails.Add(new CaseEmail
        {
            CaseId = targetCase.Id,
            EmailMessageId = message.Id,
            MatchSignal = matchResult.Signal,
            MatchDetail = matchResult.Detail,
        });

        message.CaseId = targetCase.Id;

        // §66 — every email is its own "Email Received" step in the Case timeline, at the time it arrived.
        _db.CaseEvents.Add(new CaseEvent
        {
            CaseId = targetCase.Id,
            EventType = CaseEventType.EmailReceived,
            Detail = outcome == CaseProcessOutcome.Created
                ? $"Email received from {message.FromAddress}: \"{message.Subject}\"."
                : $"Email received from {message.FromAddress}: \"{message.Subject}\" (matched to this Case via {matchResult.Signal}: {matchResult.Detail}).",
            OccurredAt = message.ReceivedAt,
        });

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // §78 idempotency — the authoritative guard is the unique index on
            // CaseEmail.EmailMessageId; if a concurrent run already linked this message, this is
            // a genuine race, not a real failure. Detach and treat as already-processed.
            var alreadyLinked = await _db.CaseEmails.AnyAsync(ce => ce.EmailMessageId == emailMessageId, cancellationToken);
            if (!alreadyLinked) throw;
            return CaseProcessOutcome.Skipped;
        }

        // §54 "Initial Notification" — start (or leave alone, if one is already Scheduled) this
        // Case's reminder cycle now that it is/remains in an ActionRequired-eligible state.
        // ScheduleInitialReminderAsync is itself idempotent and re-checks eligibility, so it is
        // safe to call unconditionally here rather than threading extra outcome-specific logic
        // through this method — same "let the specialist service decide" separation used
        // throughout (matching, verification, etc. never re-derived by their callers).
        await _reminderSchedulingService.ScheduleInitialReminderAsync(targetCase.Id, cancellationToken);

        // §72/§109 step 7 "Agent receives notification" — a brand-new Case, or one just reopened
        // by a new related customer email, is exactly the "New Email"/action-required moment §51-52
        // describes. Fires only after the above SaveChangesAsync already committed (§76 ordering);
        // best-effort, never blocks/fails this already-successful Case creation/update.
        if ((outcome == CaseProcessOutcome.Created || outcome == CaseProcessOutcome.Reopened) && _notificationService is not null)
        {
            await _notificationService.SendForCaseAsync(
                NotificationType.NewEmail, targetCase, AgentPushCommandType.ShowCase, null, cancellationToken);
        }

        return outcome;
    }

    private async Task<Case> CreateCaseAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var account = await _db.EmailAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == message.EmailAccountId, cancellationToken);

        var newCase = new Case
        {
            CaseNumber = await GenerateNextCaseNumberAsync(cancellationToken),
            EmailAccountId = message.EmailAccountId,
            CustomerEmailAddress = message.FromAddress,
            CustomerDisplayName = message.FromDisplayName,
            OwnerEmployeeId = account?.OwnerEmployeeId,
            Subject = message.Subject,
            NormalizedSubject = CaseMatchingService.NormalizeSubject(message.Subject),
            WorkStatus = CaseWorkStatus.New,
            ReplyStatus = CaseReplyStatus.AwaitingReply,
            NotificationStatus = CaseNotificationStatus.None,
            FirstEmailReceivedAt = message.ReceivedAt,
            LastActivityAt = message.ReceivedAt,
        };

        _db.Cases.Add(newCase);

        _db.CaseEvents.Add(new CaseEvent
        {
            Case = newCase,
            EventType = CaseEventType.Created,
            Detail = $"Created from email \"{message.Subject}\" from {message.FromAddress}.",
        });

        // New Cases start directly in ActionRequired, not New — §40 lists New as a status but the
        // moment a Case is created from an Important email there is, by definition, unresolved
        // action required; a message that just arrived is never "still not needing attention."
        newCase.WorkStatus = CaseWorkStatus.ActionRequired;
        _db.CaseEvents.Add(new CaseEvent
        {
            Case = newCase,
            EventType = CaseEventType.StatusChanged,
            Detail = "Work status set to ActionRequired on creation.",
        });

        return newCase;
    }

    /// <summary>
    /// Requirements §98 — sequential, human-readable Case Number. Computed from the current max
    /// rather than a DB sequence for simplicity in V1; a genuine collision under concurrent
    /// creation is caught by the CaseNumber unique index and surfaces as a DbUpdateException,
    /// which the caller's retry-on-duplicate handling above does not specifically special-case
    /// (documented as a known limitation — see tracker Assumption Log).
    /// </summary>
    private async Task<string> GenerateNextCaseNumberAsync(CancellationToken cancellationToken)
    {
        var count = await _db.Cases.CountAsync(cancellationToken);
        return $"CASE-{(count + 1):D6}";
    }

    /// <summary>Requirements §48 — completing a Case requires a reason; recorded as a history event, not a destructive edit.</summary>
    public async Task<Result<bool>> CompleteAsync(Guid caseId, CompleteCaseRequest request, CancellationToken cancellationToken)
    {
        // §84 write-boundary input validation — found missing during this session's security
        // re-verification pass (CompletionComment was the one free-text Case field with no
        // InputSanitizer call, unlike every other free-text field this session/edb11cc covered).
        if (!string.IsNullOrEmpty(request.Comment) && Iemas.Application.Common.Security.InputSanitizer.ValidateFreeText("Completion comment", request.Comment) is { } sanitizeError)
        {
            return Result<bool>.Failure(sanitizeError);
        }

        var targetCase = await _db.Cases.FirstOrDefaultAsync(c => c.Id == caseId, cancellationToken);
        if (targetCase is null)
        {
            return Result<bool>.Failure("Case not found.");
        }

        if (targetCase.WorkStatus is CaseWorkStatus.Completed or CaseWorkStatus.Cancelled)
        {
            return Result<bool>.Failure("Case is already completed or cancelled.");
        }

        targetCase.WorkStatus = CaseWorkStatus.Completed;
        targetCase.CompletionReason = request.Reason;
        targetCase.CompletionComment = request.Comment;
        targetCase.CompletedAt = DateTimeOffset.UtcNow;
        targetCase.UpdatedAt = DateTimeOffset.UtcNow;

        _db.CaseEvents.Add(new CaseEvent
        {
            CaseId = targetCase.Id,
            EventType = CaseEventType.Completed,
            Detail = $"Completed. Reason: {request.Reason}.{(string.IsNullOrWhiteSpace(request.Comment) ? "" : $" Comment: {request.Comment}")}",
        });

        await _db.SaveChangesAsync(cancellationToken);
        return Result<bool>.Success(true);
    }
}
