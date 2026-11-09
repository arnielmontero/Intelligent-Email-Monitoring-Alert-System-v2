using System.Diagnostics;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Escalations;
using Iemas.Application.Notifications;
using Iemas.Application.Operations;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Iemas.Domain.Notifications;
using Iemas.Domain.Operations;
using Iemas.Domain.Reminders;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Inquiries;

public sealed record SupervisionRunResult(int Considered, int Reminders, int WaitNotices, int Escalations, int MarkedOverdue, int Assessed, int RepliesErased, long DurationMs, IReadOnlyList<string> Actions);

/// <summary>
/// The Inquiry Supervision engine (Phases 3-4). Runs every minute. For every inquiry that is not
/// Resolved/Closed it asks <see cref="SupervisionRules"/> what is due now and applies it:
/// deadline notes, reminders, wait notices, overdue marking and management escalation. Then it lets
/// the AI assess conversations that changed (Phase 5), retries failed escalation emails and applies
/// the stored-reply retention period (Q4).
///
/// Every decision is re-derived from the database on every run (restart-safe, idempotent): the
/// per-stage episode counters on the Case are what make a reminder or escalation happen once.
/// </summary>
public class InquirySupervisionService
{
    private readonly IAppDbContext _db;
    private readonly EscalationService _escalation;
    private readonly NotificationService? _notifications;
    private readonly ResolutionAssessmentService? _assessor;
    private readonly TimeProvider _time;

    public InquirySupervisionService(
        IAppDbContext db, EscalationService escalation,
        NotificationService? notifications = null, ResolutionAssessmentService? assessor = null, TimeProvider? time = null)
    {
        _db = db;
        _escalation = escalation;
        _notifications = notifications;
        _assessor = assessor;
        _time = time ?? TimeProvider.System;
    }

    public async Task<SupervisionRunResult> RunAsync(int batchSize, CancellationToken cancellationToken, DateTimeOffset? at = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var now = at ?? _time.GetUtcNow();
        var settings = await SupervisionSettings.LoadAsync(_db, cancellationToken);
        if (!settings.Enabled)
        {
            return new SupervisionRunResult(0, 0, 0, 0, 0, 0, 0, stopwatch.ElapsedMilliseconds, Array.Empty<string>());
        }

        // §91 - while reminders are paused nothing is sent, so nothing can count toward escalation.
        var paused = await _db.IsPausedAsync(PauseControl.Reminders, cancellationToken)
            || await _db.IsPausedAsync(PauseControl.AgentNotifications, cancellationToken);

        var calendar = settings.TestClockMinutes ? BusinessCalendar.FastTestClock() : await BusinessCalendar.LoadAsync(_db, cancellationToken);
        var caseIds = await _db.Cases
            .Where(c => c.Stage != InquiryStage.Resolved && c.Stage != InquiryStage.Closed)
            .OrderBy(c => c.StageChangedAt)
            .Take(batchSize)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        int reminders = 0, waits = 0, escalations = 0, overdue = 0;
        var log = new List<string>();
        foreach (var caseId in caseIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var c = await _db.Cases.FirstAsync(x => x.Id == caseId, cancellationToken);
            var action = SupervisionRules.Evaluate(c, settings, calendar, now);
            if (action?.Kind == SupervisionActionKind.SetResolutionDue)
            {
                // Bookkeeping only: record the target and evaluate again in the same run.
                c.ResolutionDueAt = action.Value;
                await _db.SaveChangesAsync(cancellationToken);
                action = SupervisionRules.Evaluate(c, settings, calendar, now);
            }
            if (action is null) continue;

            // While paused, only silent bookkeeping happens; nothing that notifies or escalates.
            if (paused && action.Kind is not (SupervisionActionKind.SetResolutionDue or SupervisionActionKind.DeadlineMissed)) continue;

            var applied = await ApplyAsync(c, action, now, cancellationToken);
            if (applied is null) continue;
            log.Add($"{c.CaseNumber}: {applied}");
            switch (action.Kind)
            {
                case SupervisionActionKind.Reminder: reminders++; break;
                case SupervisionActionKind.WaitExpired or SupervisionActionKind.ConfirmationFollowUp: waits++; break;
                case SupervisionActionKind.Escalate: escalations++; break;
                case SupervisionActionKind.MarkOverdue: overdue++; break;
            }
        }

        var assessed = 0;
        if (_assessor is not null && settings.AiResolutionEnabled && !paused)
        {
            assessed = await _assessor.AssessChangedConversationsAsync(settings, 10, cancellationToken, now);
        }

        await _escalation.RetryFailedEscalationEmailsAsync(now, cancellationToken);
        var erased = await ApplyReplyRetentionAsync(settings, now, cancellationToken);

        return new SupervisionRunResult(caseIds.Count, reminders, waits, escalations, overdue, assessed, erased, stopwatch.ElapsedMilliseconds, log);
    }

    /// <summary>Applies one action. Returns a one-line description, or null if nothing was done.</summary>
    private async Task<string?> ApplyAsync(Case c, SupervisionAction action, DateTimeOffset now, CancellationToken cancellationToken)
    {
        switch (action.Kind)
        {
            case SupervisionActionKind.SetResolutionDue:
                c.ResolutionDueAt = action.Value;
                await _db.SaveChangesAsync(cancellationToken);
                return null;

            case SupervisionActionKind.DeadlineMissed:
                c.EpisodeDeadlineMissedAt = action.Value ?? now;
                AddEvent(c, CaseEventType.SupervisionNotice, $"Deadline missed: {action.Reason}", c.EpisodeDeadlineMissedAt.Value);
                await _db.SaveChangesAsync(cancellationToken);
                return $"deadline missed - {action.Reason}";

            case SupervisionActionKind.Reminder:
                c.EpisodeRemindersSent++;
                c.EpisodeLastReminderAt = now;
                await RecordAndNotifyAsync(c, action, now, c.EpisodeRemindersSent, cancellationToken);
                return $"reminder - {action.Reason}";

            case SupervisionActionKind.WaitExpired:
            {
                var transition = InquiryLifecycle.OnWaitExpired(c, now, action.Reason);
                if (transition is null) return null;
                AddEvent(c, CaseEventType.StageChanged, transition.Describe(), now);
                await RecordAndNotifyAsync(c, action, now, null, cancellationToken);
                return $"wait expired - {action.Reason}";
            }

            case SupervisionActionKind.ConfirmationFollowUp:
                c.EpisodeWaitNoticeAt = now;
                AddEvent(c, CaseEventType.SupervisionNotice, action.Reason, now);
                await RecordAndNotifyAsync(c, action, now, null, cancellationToken);
                return "asked the agent to review and confirm resolution";

            case SupervisionActionKind.MarkOverdue:
                c.OverdueSince = action.Value ?? now;
                InquiryLifecycle.ProjectWorkStatus(c);
                AddEvent(c, CaseEventType.SupervisionNotice, $"Overdue: {action.Reason}", now);
                await RecordAndNotifyAsync(c, action, now, null, cancellationToken);
                return $"overdue - {action.Reason}";

            case SupervisionActionKind.Escalate:
            {
                var result = await _escalation.EscalateForSupervisionAsync(c, action.Reason, now, cancellationToken);
                if (result.RetryLater) return null;

                // Counted even when nobody could be told (no policy / no recipient): the attempt is on
                // record, and repeating it every minute would only flood the history.
                c.EpisodeEscalations++;
                c.EpisodeLastEscalatedAt = now;
                if (action.IsOverdueEscalation) c.OverdueEscalatedAt = now;
                InquiryLifecycle.ProjectWorkStatus(c);
                if (result.Outcome != Domain.Escalations.EscalationOutcome.Executed)
                {
                    AddEvent(c, CaseEventType.SupervisionNotice, $"Escalation was due ({action.Reason}) but could not be delivered: {result.Detail}", now);
                }
                await _db.SaveChangesAsync(cancellationToken);
                return $"escalated - {result.Detail}";
            }
        }
        return null;
    }

    /// <summary>A Reminder row (history, counts in the CMS) plus the agent pop-up.</summary>
    private async Task RecordAndNotifyAsync(Case c, SupervisionAction action, DateTimeOffset now, int? reminderCount, CancellationToken cancellationToken)
    {
        var sequence = await _db.Reminders.CountAsync(r => r.CaseId == c.Id, cancellationToken) + 1;
        _db.Reminders.Add(new Reminder
        {
            CaseId = c.Id,
            Trigger = action.Trigger ?? ReminderTrigger.ResponseOverdue,
            Status = ReminderStatus.Sent,
            SequenceNumber = sequence,
            ScheduledForUtc = now,
            ExecutedAtUtc = now,
        });
        AddEvent(c, CaseEventType.ReminderEvent, $"Supervision notice to the agent: {action.Reason}", now);
        c.NotificationStatus = CaseNotificationStatus.Sent;
        c.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        if (_notifications is not null && action.Notification is NotificationType type)
        {
            await _notifications.SendForCaseAsync(type, c, AgentPushCommandType.ShowReminder, reminderCount, cancellationToken);
        }
    }

    /// <summary>Q4 retention: erase the stored text of agent replies once their inquiry has been closed for the retention period.</summary>
    private async Task<int> ApplyReplyRetentionAsync(SupervisionSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (settings.AgentReplyRetentionDays <= 0) return 0;
        var cutoff = now.AddDays(-settings.AgentReplyRetentionDays);
        var expired = await _db.EmailMessages
            .Where(m => m.Direction == MessageDirection.Outbound && m.BodyTextEncrypted != null && m.CaseId != null
                && _db.Cases.Any(c => c.Id == m.CaseId
                    && (c.Stage == InquiryStage.Resolved || c.Stage == InquiryStage.Closed) && c.StageChangedAt < cutoff))
            .Take(200)
            .ToListAsync(cancellationToken);
        foreach (var m in expired) m.BodyTextEncrypted = null;
        if (expired.Count > 0) await _db.SaveChangesAsync(cancellationToken);
        return expired.Count;
    }

    private void AddEvent(Case c, CaseEventType type, string detail, DateTimeOffset at) =>
        _db.CaseEvents.Add(new CaseEvent { CaseId = c.Id, EventType = type, Detail = detail, OccurredAt = at });
}
