using Iemas.Application.Common.Interfaces;
using Iemas.Domain.Agents;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Dashboard;

public record EmployeeOpenCaseCountDto(Guid EmployeeId, string EmployeeName, int OpenCaseCount);

/// <summary>Requirements §87 — every metric named in that section, computed from real, already-persisted data. Never a placeholder/mock value.</summary>
public record DashboardSummaryDto(
    int ImportantEmailsToday,
    int OpenCases,
    int AwaitingReply,
    int Overdue,
    int Escalated,
    int CompletedToday,
    int OnlineEmployees,
    int OfflineEmployees,
    List<EmployeeOpenCaseCountDto> OpenCasesByEmployee,
    int PendingAgentApprovals,
    int AiErrors,
    int EmailMonitoringErrors);

/// <summary>
/// Requirements §87 (Dashboard). Every number here is a real query against already-persisted
/// state — no cached/derived "metrics table," so the Dashboard can never show stale or
/// out-of-sync numbers relative to the actual Cases/Agents/Emails the rest of the CMS shows.
/// </summary>
public class DashboardService
{
    private readonly IAppDbContext _db;

    public DashboardService(IAppDbContext db)
    {
        _db = db;
    }

    public async Task<DashboardSummaryDto> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var todayStartUtc = DateTimeOffset.UtcNow.Date;

        // §87 "Important Emails Today" — classified Important (i.e. resulted in a Case-eligible
        // message), matching the same ImportanceDecision.Important gate CaseWorkflowService uses.
        var importantEmailsToday = await _db.EmailClassifications
            .Where(c => c.Decision == Domain.Ai.ImportanceDecision.Important)
            .Join(_db.EmailMessages.Where(m => m.ReceivedAt >= todayStartUtc), c => c.EmailMessageId, m => m.Id, (c, m) => m.Id)
            .CountAsync(cancellationToken);

        var openWorkStatuses = new[]
        {
            CaseWorkStatus.New, CaseWorkStatus.ActionRequired, CaseWorkStatus.InProgress,
            CaseWorkStatus.WaitingForCustomer, CaseWorkStatus.WaitingForInternal, CaseWorkStatus.WaitingForApproval,
            CaseWorkStatus.Overdue, CaseWorkStatus.Escalated, CaseWorkStatus.ReviewRequired,
        };

        var openCases = await _db.Cases.CountAsync(c => openWorkStatuses.Contains(c.WorkStatus), cancellationToken);
        var awaitingReply = await _db.Cases.CountAsync(c => c.ReplyStatus == CaseReplyStatus.AwaitingReply, cancellationToken);
        var overdue = await _db.Cases.CountAsync(c => c.WorkStatus == CaseWorkStatus.Overdue, cancellationToken);
        var escalated = await _db.Cases.CountAsync(c => c.WorkStatus == CaseWorkStatus.Escalated, cancellationToken);
        var completedToday = await _db.Cases.CountAsync(c => c.WorkStatus == CaseWorkStatus.Completed && c.CompletedAt >= todayStartUtc, cancellationToken);

        // §87 "Online Employees"/"Offline Employees" — an Employee counts as online if at least
        // one of their Approved Agents currently reports Connected (§13 multiple-Agent policy).
        var activeEmployeeIds = await _db.Employees.Where(e => e.IsActive).Select(e => e.Id).ToListAsync(cancellationToken);
        var onlineEmployeeIds = await _db.Agents
            .Where(a => a.RegistrationStatus == AgentRegistrationStatus.Approved && a.ConnectionStatus == AgentConnectionStatus.Connected && a.EmployeeId != null)
            .Select(a => a.EmployeeId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        var onlineEmployees = activeEmployeeIds.Count(id => onlineEmployeeIds.Contains(id));
        var offlineEmployees = activeEmployeeIds.Count - onlineEmployees;

        // §87 "Open Cases by Employee" — top 10 by open Case count, matches CMS table conventions elsewhere.
        var openCasesByEmployee = await _db.Cases
            .Where(c => openWorkStatuses.Contains(c.WorkStatus) && c.OwnerEmployeeId != null)
            .GroupBy(c => c.OwnerEmployeeId!.Value)
            .Select(g => new { EmployeeId = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(10)
            .ToListAsync(cancellationToken);
        var employeeNames = await _db.Employees
            .Where(e => openCasesByEmployee.Select(x => x.EmployeeId).Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.FullName, cancellationToken);
        var openCasesByEmployeeDtos = openCasesByEmployee
            .Select(x => new EmployeeOpenCaseCountDto(x.EmployeeId, employeeNames.GetValueOrDefault(x.EmployeeId, "Unknown"), x.Count))
            .ToList();

        var pendingAgentApprovals = await _db.Agents.CountAsync(a => a.RegistrationStatus == AgentRegistrationStatus.Pending, cancellationToken);

        // §87 "AI Errors" / "Email Monitoring Errors" — distinguishes a classification-pipeline
        // failure (ReviewRequired/AI provider exhausted retries, §83) from an intake-pipeline
        // failure (Failed — fetch/normalize/persist, §21), matching EmailProcessingStatus's own
        // documented distinction rather than conflating the two into one generic error count.
        var aiErrors = await _db.EmailMessages.CountAsync(m => m.ProcessingStatus == EmailProcessingStatus.ReviewRequired, cancellationToken);
        var emailMonitoringErrors = await _db.EmailMessages.CountAsync(m => m.ProcessingStatus == EmailProcessingStatus.Failed, cancellationToken);

        return new DashboardSummaryDto(
            importantEmailsToday, openCases, awaitingReply, overdue, escalated, completedToday,
            onlineEmployees, offlineEmployees, openCasesByEmployeeDtos, pendingAgentApprovals,
            aiErrors, emailMonitoringErrors);
    }
}
