using System.Diagnostics;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Escalations.Dtos;
using Iemas.Domain.Cases;
using Iemas.Domain.Escalations;
using Iemas.Domain.Identity;
using Iemas.Domain.Reminders;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Escalations;

/// <summary>
/// Requirements §57-§60, §63, §64 — the Escalation Engine's decision/state layer. This service
/// answers "who/what should be escalated and why," and records that decision durably (§63); it does
/// not send anything. Per the explicit Phase 9 boundary: notification/email *delivery* (§61's
/// literal SMTP send, §62's QUEUED→SENDING→SENT→DELIVERY_CONFIRMED lifecycle against a real
/// provider) is not built here — EscalationEvent's EmailMessageId/DeliveryResult/RetryCount/
/// FailureReason columns exist as pass-through fields a later delivery phase will populate, never
/// written by this service.
///
/// Built entirely on top of already-existing state (Case, Reminder, ReplyVerification's
/// Case.ReplyStatus) — this service creates no Cases, re-derives no reply/match/classification
/// decisions, and never mutates Case.ReplyStatus or Case.OwnerEmployeeId (§64: escalation does not
/// transfer ownership).
/// </summary>
public class EscalationService
{
    private readonly IAppDbContext _db;

    public EscalationService(IAppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// One evaluation cycle: every Case that still has an active, reminder-driven cycle is checked
    /// against its applicable Escalation Policy. Re-derives its candidate set fresh every call
    /// (§20 discipline, matching every prior phase's recurring job), safe to run concurrently or
    /// after a restart.
    /// </summary>
    public async Task<EscalationRunResult> RunAsync(int batchSize, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // Candidates: Cases with at least one Sent reminder (the Reminder→Escalation trigger
        // requires a reminder count) that are not yet resolved. The recheck inside EvaluateCaseAsync
        // re-validates everything §60 requires from the database, never trusted from this pre-filter.
        var candidateCaseIds = await _db.Reminders
            .Where(r => r.Status == ReminderStatus.Sent)
            .Select(r => r.CaseId)
            .Distinct()
            .Join(_db.Cases.Where(c => c.WorkStatus != CaseWorkStatus.Completed && c.WorkStatus != CaseWorkStatus.Cancelled),
                caseId => caseId, c => c.Id, (caseId, c) => c.Id)
            .OrderBy(id => id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        int executed = 0, skipped = 0, recipientUnresolved = 0, failed = 0;

        foreach (var caseId in candidateCaseIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await EvaluateCaseAsync(caseId, cancellationToken);
            switch (outcome)
            {
                case EscalationOutcome.Executed: executed++; break;
                case EscalationOutcome.Skipped: skipped++; break;
                case EscalationOutcome.RecipientUnresolved: recipientUnresolved++; break;
                case EscalationOutcome.Failed: failed++; break;
            }
        }

        stopwatch.Stop();
        return new EscalationRunResult(candidateCaseIds.Count, executed, skipped, recipientUnresolved, failed, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Evaluates and, if eligible, executes the next escalation level for one Case. Public for
    /// direct unit testing of a single Case's evaluation without a full RunAsync batch, and reused
    /// by TestPolicyAsync's dry-run.
    /// </summary>
    public async Task<EscalationOutcome> EvaluateCaseAsync(Guid caseId, CancellationToken cancellationToken)
    {
        var targetCase = await _db.Cases.FirstOrDefaultAsync(c => c.Id == caseId, cancellationToken);

        // §60.1 Case still exists.
        if (targetCase is null)
        {
            return await RecordSkipAsync(caseId, null, null, EscalationSkipReason.CaseNotFound, "Case not found.", cancellationToken);
        }

        // §60.2/§60.6/§60.7 Case is active / not completed / not cancelled.
        if (targetCase.WorkStatus is CaseWorkStatus.Completed or CaseWorkStatus.Cancelled)
        {
            return await RecordSkipAsync(caseId, null, null, EscalationSkipReason.CaseNotActive, $"Case is {targetCase.WorkStatus}.", cancellationToken);
        }

        // §60.3/§60.4/§60.5 Required work still incomplete / reply still required / no verified
        // reply has resolved the requirement. Phase 6's ReplyVerificationService is the only writer
        // of ReplyStatus — this is read-only here, never re-derived.
        if (targetCase.ReplyStatus == CaseReplyStatus.Replied)
        {
            return await RecordSkipAsync(caseId, null, null, EscalationSkipReason.ReplyVerified,
                "A verified reply has resolved the reply requirement.", cancellationToken);
        }

        var policy = await ResolvePolicyAsync(targetCase, cancellationToken);
        if (policy is null)
        {
            return await RecordSkipAsync(caseId, null, null, EscalationSkipReason.NoApplicablePolicy, "No enabled Escalation Policy applies to this Case.", cancellationToken);
        }

        // §60.9 Policy is still enabled — ResolvePolicyAsync only ever returns an enabled policy,
        // but re-asserted here defensively in case a caller reuses a stale policy reference.
        if (!policy.Enabled)
        {
            return await RecordSkipAsync(caseId, policy.Id, null, EscalationSkipReason.PolicyDisabled, "Policy is disabled.", cancellationToken);
        }

        var sentReminderCount = await _db.Reminders.CountAsync(r => r.CaseId == caseId && r.Status == ReminderStatus.Sent, cancellationToken);
        if (sentReminderCount < policy.TriggerReminderCount)
        {
            return await RecordSkipAsync(caseId, policy.Id, null, EscalationSkipReason.ThresholdNotReached,
                $"{sentReminderCount}/{policy.TriggerReminderCount} reminders sent.", cancellationToken);
        }

        if (DateTimeOffset.UtcNow - targetCase.FirstEmailReceivedAt < policy.GracePeriod)
        {
            return await RecordSkipAsync(caseId, policy.Id, null, EscalationSkipReason.ThresholdNotReached, "Grace period has not yet elapsed.", cancellationToken);
        }

        var executedLevels = await _db.EscalationEvents
            .Where(e => e.CaseId == caseId && e.EscalationPolicyId == policy.Id && e.Outcome == EscalationOutcome.Executed)
            .Select(e => e.Level)
            .ToListAsync(cancellationToken);
        var highestExecuted = executedLevels.Count > 0 ? executedLevels.Max() : 0;

        var nextLevelNumber = highestExecuted + 1;

        // §57/§58 Maximum Level.
        if (nextLevelNumber > policy.MaximumLevel)
        {
            return await RecordSkipAsync(caseId, policy.Id, highestExecuted, EscalationSkipReason.MaximumLevelReached, "Maximum escalation level already reached.", cancellationToken);
        }

        // §60.8 Escalation level has not already executed — guarded above by highestExecuted, and
        // re-confirmed here explicitly against the exact next level as a second, cheap check before
        // any further work (defends against a future refactor accidentally reusing a stale level number).
        var alreadyExecutedThisLevel = await _db.EscalationEvents.AnyAsync(
            e => e.CaseId == caseId && e.EscalationPolicyId == policy.Id && e.Level == nextLevelNumber && e.Outcome == EscalationOutcome.Executed,
            cancellationToken);
        if (alreadyExecutedThisLevel)
        {
            return await RecordSkipAsync(caseId, policy.Id, nextLevelNumber, EscalationSkipReason.LevelAlreadyExecuted, "This level has already executed for this Case.", cancellationToken);
        }

        // §57 Cooldown — since the last executed level for this Case (any policy), not just this one.
        var lastExecutedAt = await _db.EscalationEvents
            .Where(e => e.CaseId == caseId && e.Outcome == EscalationOutcome.Executed)
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => (DateTimeOffset?)e.OccurredAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (lastExecutedAt is DateTimeOffset last && DateTimeOffset.UtcNow - last < policy.Cooldown)
        {
            return await RecordSkipAsync(caseId, policy.Id, nextLevelNumber, EscalationSkipReason.CooldownActive, "Cooldown since the last escalation has not yet elapsed.", cancellationToken);
        }

        var level = await _db.EscalationLevels
            .Include(l => l.SpecificEmployee)
            .Include(l => l.SpecificGroup!).ThenInclude(g => g.Members).ThenInclude(m => m.Employee)
            .FirstOrDefaultAsync(l => l.EscalationPolicyId == policy.Id && l.Level == nextLevelNumber, cancellationToken);
        if (level is null)
        {
            // Configured MaximumLevel exceeds the number of Levels actually defined — treat as
            // "no further level," not a crash.
            return await RecordSkipAsync(caseId, policy.Id, nextLevelNumber, EscalationSkipReason.MaximumLevelReached, "No Level is configured at this number.", cancellationToken);
        }

        if (level.DelayAfterPreviousLevel > TimeSpan.Zero)
        {
            var sinceEligible = lastExecutedAt ?? targetCase.FirstEmailReceivedAt.Add(policy.GracePeriod);
            if (DateTimeOffset.UtcNow - sinceEligible < level.DelayAfterPreviousLevel)
            {
                return await RecordSkipAsync(caseId, policy.Id, nextLevelNumber, EscalationSkipReason.ThresholdNotReached, $"Level {nextLevelNumber}'s own delay has not yet elapsed.", cancellationToken);
            }
        }

        // §60.10 Recipient resolves successfully.
        var recipient = await ResolveRecipientAsync(targetCase, level, cancellationToken);
        if (recipient is null)
        {
            return await RecordEventAsync(targetCase, policy, level, EscalationOutcome.RecipientUnresolved, null,
                $"Recipient of type {level.RecipientType} could not be resolved.", cancellationToken);
        }

        var triggerDetail = $"Reminder threshold {policy.TriggerReminderCount} reached ({sentReminderCount} sent); grace period and level delay elapsed.";
        return await RecordEventAsync(targetCase, policy, level, EscalationOutcome.Executed, recipient, triggerDetail, cancellationToken, trigger: triggerDetail);
    }

    /// <summary>
    /// Dry-run evaluation for the CMS "Test Policy" (§57) action — runs the same eligibility logic
    /// against a specific Case without recording any EscalationEvent, so an administrator can check
    /// "what would happen" without polluting the audit trail with test data.
    /// </summary>
    public async Task<TestEscalationPolicyResult> TestPolicyAsync(Guid policyId, Guid caseId, CancellationToken cancellationToken)
    {
        var policy = await _db.EscalationPolicies.Include(p => p.Levels).FirstOrDefaultAsync(p => p.Id == policyId, cancellationToken);
        if (policy is null) return new TestEscalationPolicyResult(false, null, null, "Policy not found.");

        var targetCase = await _db.Cases.FirstOrDefaultAsync(c => c.Id == caseId, cancellationToken);
        if (targetCase is null) return new TestEscalationPolicyResult(false, null, null, "Case not found.");

        if (targetCase.WorkStatus is CaseWorkStatus.Completed or CaseWorkStatus.Cancelled)
        {
            return new TestEscalationPolicyResult(false, null, null, $"Case is {targetCase.WorkStatus} — would not escalate.");
        }
        if (targetCase.ReplyStatus == CaseReplyStatus.Replied)
        {
            return new TestEscalationPolicyResult(false, null, null, "Case has a verified reply — would not escalate.");
        }
        if (!policy.Enabled)
        {
            return new TestEscalationPolicyResult(false, null, null, "Policy is disabled.");
        }

        var sentReminderCount = await _db.Reminders.CountAsync(r => r.CaseId == caseId && r.Status == ReminderStatus.Sent, cancellationToken);
        if (sentReminderCount < policy.TriggerReminderCount)
        {
            return new TestEscalationPolicyResult(false, null, null, $"Only {sentReminderCount}/{policy.TriggerReminderCount} reminders sent — threshold not reached.");
        }

        if (DateTimeOffset.UtcNow - targetCase.FirstEmailReceivedAt < policy.GracePeriod)
        {
            return new TestEscalationPolicyResult(false, null, null, "Grace period has not yet elapsed — would not escalate yet.");
        }

        var level = policy.Levels.OrderBy(l => l.Level).FirstOrDefault();
        if (level is null)
        {
            return new TestEscalationPolicyResult(false, null, null, "Policy has no Levels configured.");
        }

        var recipient = await ResolveRecipientAsync(targetCase, level, cancellationToken);
        return recipient is null
            ? new TestEscalationPolicyResult(false, level.Level, null, $"Level {level.Level} recipient ({level.RecipientType}) could not be resolved.")
            : new TestEscalationPolicyResult(true, level.Level, recipient, $"Would escalate to Level {level.Level}: {recipient}.");
    }

    /// <summary>§59 — recipient resolution, using organizational data where possible. Returns a display string, or null if unresolvable (§60.10).</summary>
    private async Task<string?> ResolveRecipientAsync(Case targetCase, EscalationLevel level, CancellationToken cancellationToken)
    {
        switch (level.RecipientType)
        {
            case EscalationRecipientType.Employee:
            {
                if (targetCase.OwnerEmployeeId is not Guid ownerId) return null;
                var owner = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == ownerId && e.IsActive, cancellationToken);
                return owner is null ? null : $"{owner.FullName} <{owner.Email}>";
            }
            case EscalationRecipientType.EmployeeSupervisor:
            {
                if (targetCase.OwnerEmployeeId is not Guid ownerId) return null;
                var owner = await _db.Employees.AsNoTracking().Include(e => e.SupervisorEmployee).FirstOrDefaultAsync(e => e.Id == ownerId, cancellationToken);
                var supervisor = owner?.SupervisorEmployee;
                return supervisor is null || !supervisor.IsActive ? null : $"{supervisor.FullName} <{supervisor.Email}>";
            }
            case EscalationRecipientType.DepartmentManager:
            {
                if (targetCase.OwnerEmployeeId is not Guid ownerId) return null;
                var owner = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == ownerId, cancellationToken);
                if (owner?.DepartmentId is not Guid deptId) return null;
                var dept = await _db.Departments.AsNoTracking().Include(d => d.ManagerEmployee).FirstOrDefaultAsync(d => d.Id == deptId, cancellationToken);
                var manager = dept?.ManagerEmployee;
                return manager is null || !manager.IsActive ? null : $"{manager.FullName} <{manager.Email}>";
            }
            case EscalationRecipientType.SpecificEmployee:
            {
                if (level.SpecificEmployeeId is not Guid empId) return null;
                var employee = level.SpecificEmployee ?? await _db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == empId, cancellationToken);
                return employee is null || !employee.IsActive ? null : $"{employee.FullName} <{employee.Email}>";
            }
            case EscalationRecipientType.SpecificGroup:
            {
                if (level.SpecificGroupId is not Guid groupId) return null;
                var group = level.SpecificGroup ?? await _db.EscalationGroups.AsNoTracking()
                    .Include(g => g.Members).ThenInclude(m => m.Employee)
                    .FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken);
                var activeMembers = group?.Members.Where(m => m.Employee.IsActive).ToList();
                if (activeMembers is null || activeMembers.Count == 0) return null;
                return string.Join("; ", activeMembers.Select(m => $"{m.Employee.FullName} <{m.Employee.Email}>"));
            }
            default:
                return null;
        }
    }

    /// <summary>§57 policy resolution — profile-scoped policy takes precedence over the default, same precedent as Phase 8's ReminderPolicy resolution. Category/Priority further narrow a profile-scoped match when the policy specifies them.</summary>
    private async Task<EscalationPolicy?> ResolvePolicyAsync(Case targetCase, CancellationToken cancellationToken)
    {
        var caseEmailMessageIds = _db.CaseEmails.Where(ce => ce.CaseId == targetCase.Id).Select(ce => ce.EmailMessageId);
        var classification = await _db.EmailClassifications.AsNoTracking()
            .Where(c => caseEmailMessageIds.Contains(c.EmailMessageId))
            .Select(c => new { c.ClassificationProfileId, c.Category, c.Priority })
            .FirstOrDefaultAsync(cancellationToken);

        if (classification?.ClassificationProfileId is Guid profileId)
        {
            var scopedCandidates = await _db.EscalationPolicies.Include(p => p.Levels)
                .Where(p => p.Enabled && p.ClassificationProfileId == profileId)
                .ToListAsync(cancellationToken);

            var match = scopedCandidates.FirstOrDefault(p =>
                (p.Priority is null || p.Priority == classification.Priority) &&
                (string.IsNullOrWhiteSpace(p.Categories) || MatchesCategory(p.Categories, classification.Category)));
            if (match is not null) return match;
        }

        return await _db.EscalationPolicies.Include(p => p.Levels).FirstOrDefaultAsync(p => p.Enabled && p.IsDefault, cancellationToken);
    }

    private static bool MatchesCategory(string categoriesCsv, string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return false;
        return categoriesCsv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// §60/§89 — a Skipped attempt IS recorded as an EscalationEvent (so "why hasn't this Case
    /// escalated yet" is answerable via the API/CMS), but deliberately does NOT append a CaseEvent
    /// to the Case History timeline. §60's recheck conditions — especially ThresholdNotReached —
    /// recur every single poll for every still-ineligible Case; surfacing each one on the
    /// customer-facing Case History timeline would flood it with noise no investigator wants there
    /// (unlike Reminder's Cancelled rows, each of which represents a real one-time state change).
    /// EscalationEvent rows themselves are cheap and queryable independently via RemindersController's
    /// escalation-events analog, so nothing is actually lost — only kept off the Case History view.
    /// </summary>
    private async Task<EscalationOutcome> RecordSkipAsync(
        Guid caseId, Guid? policyId, int? level, EscalationSkipReason reason, string detail, CancellationToken cancellationToken)
    {
        _db.EscalationEvents.Add(new EscalationEvent
        {
            CaseId = caseId,
            EscalationPolicyId = policyId,
            Level = level ?? 0,
            Trigger = detail,
            Outcome = EscalationOutcome.Skipped,
            SkipReason = reason,
            Detail = detail,
        });

        await _db.SaveChangesAsync(cancellationToken);
        return EscalationOutcome.Skipped;
    }

    private async Task<EscalationOutcome> RecordEventAsync(
        Case targetCase, EscalationPolicy policy, EscalationLevel level, EscalationOutcome outcome, string? recipientDisplay,
        string detail, CancellationToken cancellationToken, string? trigger = null)
    {
        var escalationEvent = new EscalationEvent
        {
            CaseId = targetCase.Id,
            EscalationPolicyId = policy.Id,
            Level = level.Level,
            Trigger = trigger ?? detail,
            RecipientType = level.RecipientType,
            RecipientDisplay = recipientDisplay,
            RecipientEmployeeId = level.RecipientType == EscalationRecipientType.SpecificEmployee ? level.SpecificEmployeeId : null,
            Channel = policy.Channel,
            Outcome = outcome,
            Detail = detail,
        };
        _db.EscalationEvents.Add(escalationEvent);

        // §63 "Every escalation becomes a Case History child event" — appended to the same
        // append-only timeline Phases 5-8 already write to, not a separate escalation-only log.
        _db.CaseEvents.Add(new CaseEvent
        {
            CaseId = targetCase.Id,
            EventType = CaseEventType.EscalationEvent,
            Detail = outcome == EscalationOutcome.Executed
                ? $"Escalated to Level {level.Level} ({level.RecipientType}): {recipientDisplay}."
                : $"Escalation to Level {level.Level} could not resolve a recipient ({level.RecipientType}).",
        });

        // §60.2/§41 — escalation may move a still-outstanding Case's WorkStatus to Escalated so it
        // is visible as such (§40 lists Escalated as a distinct WorkStatus value), but this NEVER
        // touches OwnerEmployeeId (§64) or ReplyStatus (owned exclusively by Phase 6).
        if (outcome == EscalationOutcome.Executed && targetCase.WorkStatus != CaseWorkStatus.Escalated)
        {
            targetCase.WorkStatus = CaseWorkStatus.Escalated;
            targetCase.UpdatedAt = DateTimeOffset.UtcNow;
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent run already recorded this exact level — treat as already-handled, same
            // race-handling pattern used throughout (EmailIntakeService, CaseWorkflowService, etc.).
            var raced = await _db.EscalationEvents.AsNoTracking().AnyAsync(
                e => e.CaseId == targetCase.Id && e.EscalationPolicyId == policy.Id && e.Level == level.Level && e.Outcome == EscalationOutcome.Executed,
                cancellationToken);
            if (!raced) throw;
            return EscalationOutcome.Skipped;
        }

        return outcome;
    }
}
