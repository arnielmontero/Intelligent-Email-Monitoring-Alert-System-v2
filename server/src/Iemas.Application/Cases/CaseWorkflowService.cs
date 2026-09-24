using System.Diagnostics;
using Iemas.Application.Cases.Dtos;
using Iemas.Application.Common;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Reminders;
using Iemas.Domain.Ai;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Cases;

public record CaseRunResult(int ConsideredCount, int CreatedCount, int UpdatedCount, int ReopenedCount, long DurationMs);

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
    private readonly IAgentNotificationDispatcher? _dispatcher;

    public CaseWorkflowService(
        IAppDbContext db,
        CaseMatchingService matchingService,
        ReminderSchedulingService reminderSchedulingService,
        IAgentNotificationDispatcher? dispatcher = null)
    {
        _db = db;
        _matchingService = matchingService;
        _reminderSchedulingService = reminderSchedulingService;
        _dispatcher = dispatcher;
    }

    public async Task<CaseRunResult> RunAsync(int batchSize, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // §20 "Scheduled jobs must re-check current state" — the candidate set is read fresh
        // each run; a message picked up by RunAsync always re-validates its own state inside
        // ProcessOneAsync before acting, exactly like EmailIntakeService/EmailClassificationService.
        var candidateIds = await _db.EmailClassifications
            .Where(c => c.Decision == ImportanceDecision.Important)
            .Join(_db.EmailMessages.Where(m => m.ProcessingStatus == EmailProcessingStatus.Processed),
                c => c.EmailMessageId, m => m.Id, (c, m) => m.Id)
            .Where(messageId => !_db.CaseEmails.Any(ce => ce.EmailMessageId == messageId))
            .OrderBy(id => id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        int created = 0, updated = 0, reopened = 0;

        foreach (var messageId in candidateIds)
        {
            var outcome = await ProcessOneAsync(messageId, cancellationToken);
            switch (outcome)
            {
                case CaseProcessOutcome.Created: created++; break;
                case CaseProcessOutcome.Updated: updated++; break;
                case CaseProcessOutcome.Reopened: reopened++; break;
            }
        }

        stopwatch.Stop();
        return new CaseRunResult(candidateIds.Count, created, updated, reopened, stopwatch.ElapsedMilliseconds);
    }

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

                _db.CaseEvents.Add(new CaseEvent
                {
                    CaseId = targetCase.Id,
                    EventType = CaseEventType.Updated,
                    Detail = $"New related email added (matched via {matchResult.Signal}: {matchResult.Detail}).",
                });
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
        if ((outcome == CaseProcessOutcome.Created || outcome == CaseProcessOutcome.Reopened)
            && _dispatcher is not null && targetCase.OwnerEmployeeId is Guid ownerEmployeeId)
        {
            await _dispatcher.NotifyEmployeeAsync(
                ownerEmployeeId,
                new AgentPushCommand(
                    AgentPushCommandType.ShowCase,
                    targetCase.Id,
                    targetCase.CaseNumber,
                    "New Email",
                    $"You have a new email from {targetCase.CustomerEmailAddress}: \"{targetCase.Subject}\". Please check."),
                cancellationToken);
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
