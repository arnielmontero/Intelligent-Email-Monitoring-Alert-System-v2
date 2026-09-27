using Iemas.Application.Common.Interfaces;
using Iemas.Domain.Agents;
using Iemas.Domain.Cases;
using Iemas.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.EmployeeActivity;

public record EmployeeActivityDto(
    Guid Id,
    DateTimeOffset OccurredAt,
    DateTimeOffset ClientTimestamp,
    Guid EmployeeId,
    string EmployeeName,
    Guid CaseId,
    string CaseNumber,
    string CaseSubject,
    string Activity,
    string? Comment,
    Guid AgentId,
    string AgentName);

public record EmployeeActivitySummaryDto(
    Guid EmployeeId,
    string EmployeeName,
    bool IsActive,
    int ActionCount,
    int AcknowledgedCount,
    int CompletedCount,
    int CommentCount,
    int NotificationCount,
    int NotificationsAcknowledgedCount,
    int OpenCaseCount,
    DateTimeOffset? LastActivityAt,
    int ConnectedAgentCount);

/// <summary>
/// Requirements §67 "Employee Activity — what did the employee do?". Read-only view over the
/// Agent-submitted actions (AgentCaseAction), kept separate from Case History (what happened to
/// the Case), the System Audit Log (administrative changes) and the technical Agent log.
/// </summary>
public class EmployeeActivityQueryService
{
    public const string CommentActivity = "Comment";

    private readonly IAppDbContext _db;

    public EmployeeActivityQueryService(IAppDbContext db)
    {
        _db = db;
    }

    public async Task<List<EmployeeActivityDto>> SearchAsync(
        Guid? employeeId, Guid? caseId, string? activity, DateTimeOffset? from, DateTimeOffset? to, int take,
        CancellationToken cancellationToken)
    {
        // A standalone comment is stored as an AgentCaseAction whose Case History event is EmployeeComment.
        var query = _db.AgentCaseActions.AsNoTracking().Select(a => new
        {
            a.Id,
            a.ServerTimestamp,
            a.ClientTimestamp,
            a.EmployeeId,
            EmployeeName = a.Employee.FullName,
            a.CaseId,
            a.Case.CaseNumber,
            CaseSubject = a.Case.Subject,
            a.ActionType,
            IsComment = _db.CaseEvents.Any(e => e.Id == a.CaseEventId && e.EventType == CaseEventType.EmployeeComment),
            a.Comment,
            a.AgentId,
            AgentName = a.Agent.ClientName,
        });

        if (employeeId is Guid employee) query = query.Where(a => a.EmployeeId == employee);
        if (caseId is Guid caseValue) query = query.Where(a => a.CaseId == caseValue);
        if (from is DateTimeOffset fromValue) query = query.Where(a => a.ServerTimestamp >= fromValue);
        if (to is DateTimeOffset toValue) query = query.Where(a => a.ServerTimestamp <= toValue);

        if (!string.IsNullOrWhiteSpace(activity))
        {
            if (string.Equals(activity, CommentActivity, StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(a => a.IsComment);
            }
            else if (Enum.TryParse<CaseActionType>(activity, true, out var actionType))
            {
                query = query.Where(a => !a.IsComment && a.ActionType == actionType);
            }
        }

        var rows = await query
            .OrderByDescending(a => a.ServerTimestamp)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(cancellationToken);

        return rows.Select(r => new EmployeeActivityDto(
            r.Id, r.ServerTimestamp, r.ClientTimestamp, r.EmployeeId, r.EmployeeName, r.CaseId, r.CaseNumber, r.CaseSubject,
            r.IsComment ? CommentActivity : r.ActionType.ToString(), r.Comment, r.AgentId, r.AgentName)).ToList();
    }

    public async Task<List<EmployeeActivitySummaryDto>> GetSummaryAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var employees = await _db.Employees.AsNoTracking()
            .OrderBy(e => e.FullName)
            .Select(e => new { e.Id, e.FullName, e.IsActive })
            .ToListAsync(cancellationToken);

        var actions = await _db.AgentCaseActions.AsNoTracking()
            .Where(a => a.ServerTimestamp >= from && a.ServerTimestamp <= to)
            .Select(a => new
            {
                a.EmployeeId,
                a.ActionType,
                a.ServerTimestamp,
                IsComment = _db.CaseEvents.Any(e => e.Id == a.CaseEventId && e.EventType == CaseEventType.EmployeeComment),
            })
            .ToListAsync(cancellationToken);

        var notifications = await _db.Notifications.AsNoTracking()
            .Where(n => n.CreatedAt >= from && n.CreatedAt <= to)
            .Select(n => new { n.EmployeeId, n.Status })
            .ToListAsync(cancellationToken);

        var openCaseOwners = await _db.Cases.AsNoTracking()
            .Where(c => c.OwnerEmployeeId != null
                        && c.WorkStatus != CaseWorkStatus.Completed && c.WorkStatus != CaseWorkStatus.Cancelled
                        && c.WorkStatus != CaseWorkStatus.Expired)
            .Select(c => c.OwnerEmployeeId!.Value)
            .ToListAsync(cancellationToken);

        var connectedAgentOwners = await _db.Agents.AsNoTracking()
            .Where(a => a.EmployeeId != null && a.RegistrationStatus == AgentRegistrationStatus.Approved
                        && a.ConnectionStatus == AgentConnectionStatus.Connected)
            .Select(a => a.EmployeeId!.Value)
            .ToListAsync(cancellationToken);

        return employees.Select(e =>
        {
            var own = actions.Where(a => a.EmployeeId == e.Id).ToList();
            var ownNotifications = notifications.Where(n => n.EmployeeId == e.Id).ToList();
            return new EmployeeActivitySummaryDto(
                e.Id, e.FullName, e.IsActive,
                own.Count(a => !a.IsComment),
                own.Count(a => !a.IsComment && a.ActionType == CaseActionType.Acknowledged),
                own.Count(a => !a.IsComment && a.ActionType == CaseActionType.MarkCompleted),
                own.Count(a => a.IsComment),
                ownNotifications.Count,
                ownNotifications.Count(n => n.Status == NotificationDeliveryStatus.Acknowledged),
                openCaseOwners.Count(id => id == e.Id),
                own.Count > 0 ? own.Max(a => a.ServerTimestamp) : null,
                connectedAgentOwners.Count(id => id == e.Id));
        }).ToList();
    }
}
