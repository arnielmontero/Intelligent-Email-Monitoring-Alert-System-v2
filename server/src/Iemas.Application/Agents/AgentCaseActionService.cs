using Iemas.Application.Agents.Dtos;
using Iemas.Application.Cases;
using Iemas.Application.Cases.Dtos;
using Iemas.Application.Common;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Reminders;
using Iemas.Domain.Agents;
using Iemas.Domain.Cases;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Agents;

/// <summary>
/// Requirements §46 (Employee Actions), §47 (Employee Comments), §43 (Already Replied claim vs.
/// verified fact), §73 (Agent-to-Server Actions, incl. idempotency + authorization validation),
/// §78 (idempotency).
///
/// Every action here is called with an already-authenticated Agent's own AgentId/EmployeeId
/// (never from the request body — §73 "Server must validate that the Agent is authorized for the
/// Employee and Case"), so there is no code path where an Agent can act as a different Employee.
///
/// §46 hard requirement: "Employee actions are events/requests. They are not automatically
/// authoritative facts." Reaffirmed most sharply for ALREADY_REPLIED (§43): this method only ever
/// records the claim as a CaseEvent — it never touches Case.ReplyStatus. Only Phase 6's
/// ReplyVerificationService, inspecting the real Sent mailbox, may set VerifiedReply/NoReplyFound/
/// VerificationPending/VerificationFailed. This is verified structurally, not just by convention:
/// no line in this file assigns to Case.ReplyStatus at all.
/// </summary>
public class AgentCaseActionService
{
    private readonly IAppDbContext _db;
    private readonly CaseWorkflowService _caseWorkflowService;
    private readonly ReminderSchedulingService _reminderSchedulingService;

    public AgentCaseActionService(IAppDbContext db, CaseWorkflowService caseWorkflowService, ReminderSchedulingService reminderSchedulingService)
    {
        _db = db;
        _caseWorkflowService = caseWorkflowService;
        _reminderSchedulingService = reminderSchedulingService;
    }

    public async Task<Result<CaseActionResultDto>> SubmitActionAsync(
        Guid agentId, Guid employeeId, SubmitCaseActionRequest request, CancellationToken cancellationToken)
    {
        // §78/§73 idempotency — a retried request with the same (Agent, RequestId) pair must
        // return the same outcome, never create a second CaseEvent or apply the action twice.
        var existing = await _db.AgentCaseActions
            .FirstOrDefaultAsync(a => a.AgentId == agentId && a.RequestId == request.RequestId, cancellationToken);
        if (existing is not null)
        {
            var existingCase = await _db.Cases.AsNoTracking().FirstAsync(c => c.Id == existing.CaseId, cancellationToken);
            return Result<CaseActionResultDto>.Success(new CaseActionResultDto(
                existingCase.Id, existingCase.WorkStatus.ToString(), existingCase.ReplyStatus.ToString(), WasIdempotentReplay: true));
        }

        var targetCase = await _db.Cases.FirstOrDefaultAsync(c => c.Id == request.CaseId, cancellationToken);
        if (targetCase is null)
        {
            return Result<CaseActionResultDto>.Failure("Case not found.");
        }

        // §73 "Server must validate that the Agent is authorized for the Employee and Case" — an
        // Agent may only act on Cases owned by its own linked Employee.
        if (targetCase.OwnerEmployeeId != employeeId)
        {
            return Result<CaseActionResultDto>.Failure("This Agent's employee does not own this Case.");
        }

        var actionRecord = new AgentCaseAction
        {
            RequestId = request.RequestId,
            AgentId = agentId,
            EmployeeId = employeeId,
            CaseId = targetCase.Id,
            ActionType = request.ActionType,
            Comment = request.Comment,
            ClientTimestamp = request.ClientTimestamp,
        };

        var detail = BuildActionDetail(request.ActionType, request.Comment);
        ApplyAction(targetCase, request.ActionType);
        targetCase.UpdatedAt = DateTimeOffset.UtcNow;

        var caseEvent = new CaseEvent
        {
            CaseId = targetCase.Id,
            EventType = CaseEventType.EmployeeAction,
            Detail = detail,
            ActorEmployeeId = employeeId,
        };
        _db.CaseEvents.Add(caseEvent);
        _db.AgentCaseActions.Add(actionRecord);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // §78 — the unique (AgentId, RequestId) index is the authoritative idempotency guard
            // for a genuine concurrent replay, same pattern as EmailIntakeService/CaseWorkflowService.
            var raced = await _db.AgentCaseActions.AsNoTracking()
                .FirstOrDefaultAsync(a => a.AgentId == agentId && a.RequestId == request.RequestId, cancellationToken);
            if (raced is null) throw;

            var reloadedCase = await _db.Cases.AsNoTracking().FirstAsync(c => c.Id == raced.CaseId, cancellationToken);
            return Result<CaseActionResultDto>.Success(new CaseActionResultDto(
                reloadedCase.Id, reloadedCase.WorkStatus.ToString(), reloadedCase.ReplyStatus.ToString(), WasIdempotentReplay: true));
        }

        actionRecord.CaseEventId = caseEvent.Id;
        await _db.SaveChangesAsync(cancellationToken);

        // §56 "Remind me at 3:00 PM" — only after the AgentCaseAction row is durably persisted
        // (so SourceAgentCaseActionId's idempotency guard has something real to reference). A
        // failure to schedule here is deliberately non-fatal to the action itself: the employee's
        // RemindLater request was still recorded; ReminderSchedulingService's own eligibility
        // checks decide whether a Reminder makes sense (e.g. a Case that became ineligible in the
        // instant between ApplyAction and here would simply not get a Reminder, not an error).
        if (request.ActionType == CaseActionType.RemindLater)
        {
            await _reminderSchedulingService.ScheduleEmployeeRequestedReminderAsync(
                targetCase.Id, actionRecord.Id, request.RequestedForUtc, cancellationToken);
        }

        return Result<CaseActionResultDto>.Success(new CaseActionResultDto(
            targetCase.Id, targetCase.WorkStatus.ToString(), targetCase.ReplyStatus.ToString(), WasIdempotentReplay: false));
    }

    /// <summary>§47 — a standalone comment, not tied to a status-changing action.</summary>
    public async Task<Result<bool>> SubmitCommentAsync(Guid agentId, Guid employeeId, SubmitCaseCommentRequest request, CancellationToken cancellationToken)
    {
        var existing = await _db.AgentCaseActions
            .AnyAsync(a => a.AgentId == agentId && a.RequestId == request.RequestId, cancellationToken);
        if (existing)
        {
            return Result<bool>.Success(true);
        }

        var targetCase = await _db.Cases.FirstOrDefaultAsync(c => c.Id == request.CaseId, cancellationToken);
        if (targetCase is null)
        {
            return Result<bool>.Failure("Case not found.");
        }

        if (targetCase.OwnerEmployeeId != employeeId)
        {
            return Result<bool>.Failure("This Agent's employee does not own this Case.");
        }

        if (string.IsNullOrWhiteSpace(request.Comment))
        {
            return Result<bool>.Failure("A comment is required.");
        }

        var caseEvent = new CaseEvent
        {
            CaseId = targetCase.Id,
            EventType = CaseEventType.EmployeeComment,
            Detail = request.Comment,
            ActorEmployeeId = employeeId,
        };
        _db.CaseEvents.Add(caseEvent);

        _db.AgentCaseActions.Add(new AgentCaseAction
        {
            RequestId = request.RequestId,
            AgentId = agentId,
            EmployeeId = employeeId,
            CaseId = targetCase.Id,
            ActionType = CaseActionType.Acknowledged, // comments carry no ActionType of their own; recorded distinctly via CaseEventType.EmployeeComment above
            Comment = request.Comment,
            ClientTimestamp = request.ClientTimestamp,
        });

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            var raced = await _db.AgentCaseActions.AnyAsync(a => a.AgentId == agentId && a.RequestId == request.RequestId, cancellationToken);
            if (!raced) throw;
        }

        return Result<bool>.Success(true);
    }

    /// <summary>
    /// §46/§43 — applies the deterministic, non-AI, non-authoritative-beyond-its-own-scope effect
    /// of each action. Every branch is a plain Case-field mutation; none of them call into
    /// classification, matching, or reply-verification logic — those remain owned by their own
    /// phases. ALREADY_REPLIED intentionally has no Case-mutating effect at all (see class doc).
    /// </summary>
    private static void ApplyAction(Case targetCase, CaseActionType actionType)
    {
        switch (actionType)
        {
            case CaseActionType.Acknowledged:
                // §41 Critical Status Rule — acknowledgement is its own event; it must never be
                // conflated with Replied/Completed. No WorkStatus/ReplyStatus field represents
                // "acknowledged" at all, so nothing here mutates Case state beyond the CaseEvent
                // this action already produces.
                break;

            case CaseActionType.WillHandle:
                if (targetCase.WorkStatus is CaseWorkStatus.New or CaseWorkStatus.ActionRequired)
                {
                    targetCase.WorkStatus = CaseWorkStatus.InProgress;
                }
                break;

            case CaseActionType.AlreadyReplied:
                // §43 — deliberately no Case mutation. The claim is recorded as an event only
                // (already done by the caller); Case.ReplyStatus is untouched here and remains
                // whatever Phase 6's ReplyVerificationService last set it to. Phase 6 should be
                // triggered to (re)check — that trigger is left to the recurring job's own
                // schedule/candidate query rather than this service reaching into Phase 6 directly,
                // keeping the phases decoupled exactly as they've been kept decoupled throughout.
                break;

            case CaseActionType.WaitingForCustomer:
                targetCase.WorkStatus = CaseWorkStatus.WaitingForCustomer;
                break;

            case CaseActionType.WaitingForInternal:
                targetCase.WorkStatus = CaseWorkStatus.WaitingForInternal;
                break;

            case CaseActionType.RemindLater:
                // §56 — no Case-field mutation here; actual Reminder scheduling happens in
                // SubmitActionAsync after this action is persisted (see the call to
                // ReminderSchedulingService there), since scheduling needs the durably-saved
                // AgentCaseAction.Id for its idempotency key.
                break;

            case CaseActionType.MarkCompleted:
                // Completion requires a reason (§48) via the dedicated CasesController/complete
                // endpoint and CaseWorkflowService.CompleteAsync, not this generic action path —
                // a bare MARK_COMPLETED action without a reason is intentionally not sufficient
                // to complete a Case; the Agent should invoke the reason-requiring endpoint.
                break;

            case CaseActionType.Cancel:
                if (targetCase.WorkStatus is not (CaseWorkStatus.Completed or CaseWorkStatus.Cancelled))
                {
                    targetCase.WorkStatus = CaseWorkStatus.Cancelled;
                }
                break;

            case CaseActionType.Reopen:
                if (targetCase.WorkStatus is CaseWorkStatus.Completed or CaseWorkStatus.Cancelled)
                {
                    targetCase.WorkStatus = CaseWorkStatus.ActionRequired;
                    targetCase.ReopenCount++;
                }
                break;

            case CaseActionType.RequestEscalation:
                // §57-§60 Escalation Policies/Conditions are a later phase; this only records the
                // employee's request as a CaseEvent. No WorkStatus.Escalated transition happens
                // here — that belongs to the actual Escalation Engine evaluating its own conditions.
                break;
        }
    }

    private static string BuildActionDetail(CaseActionType actionType, string? comment)
    {
        var baseDetail = actionType switch
        {
            CaseActionType.Acknowledged => "Employee acknowledged the Case.",
            CaseActionType.WillHandle => "Employee indicated they will handle this Case.",
            CaseActionType.AlreadyReplied => "Employee claims to have already replied. This is a claim, not a verified fact — see Reply Verification history for the actual mailbox-checked outcome.",
            CaseActionType.WaitingForCustomer => "Employee marked the Case as waiting for the customer.",
            CaseActionType.WaitingForInternal => "Employee marked the Case as waiting on an internal party.",
            CaseActionType.RemindLater => "Employee requested a later reminder.",
            CaseActionType.MarkCompleted => "Employee requested completion (use the Complete Case action with a reason to actually complete).",
            CaseActionType.Cancel => "Employee cancelled the Case.",
            CaseActionType.Reopen => "Employee reopened the Case.",
            CaseActionType.RequestEscalation => "Employee requested escalation.",
            _ => actionType.ToString(),
        };

        return string.IsNullOrWhiteSpace(comment) ? baseDetail : $"{baseDetail} Comment: {comment}";
    }
}
