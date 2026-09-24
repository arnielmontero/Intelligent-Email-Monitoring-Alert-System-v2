using Hangfire;
using Iemas.Application.Dashboard;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Iemas.Api.Controllers;

/// <summary>§87's "Background Job Failures" — Hangfire's monitoring API is an Api-layer concern (Iemas.Application deliberately takes no Hangfire package reference, same module-boundary rule SystemHealthController/RecurringJobGuards already follow), so this extends the Application-layer summary with that one field here.</summary>
public record DashboardSummaryWithJobsDto(
    int ImportantEmailsToday, int OpenCases, int AwaitingReply, int Overdue, int Escalated, int CompletedToday,
    int OnlineEmployees, int OfflineEmployees, List<EmployeeOpenCaseCountDto> OpenCasesByEmployee,
    int PendingAgentApprovals, int AiErrors, int EmailMonitoringErrors, int BackgroundJobFailures);

/// <summary>Requirements §87 (Dashboard), §86 (top-level nav). Read-only aggregate; every number backed by a real query, no mock/placeholder values.</summary>
[ApiController]
[Route("api/v1/dashboard")]
[Authorize(Policy = "RequireAuditorOrAbove")]
public class DashboardController : ControllerBase
{
    private readonly DashboardService _service;

    public DashboardController(DashboardService service)
    {
        _service = service;
    }

    [HttpGet("summary")]
    public async Task<ActionResult<DashboardSummaryWithJobsDto>> GetSummary(CancellationToken cancellationToken)
    {
        var summary = await _service.GetSummaryAsync(cancellationToken);

        int backgroundJobFailures = 0;
        try
        {
            var monitor = JobStorage.Current.GetMonitoringApi();
            backgroundJobFailures = (int)monitor.FailedCount();
        }
        catch
        {
            // Left at 0 — a Hangfire storage hiccup must not fail the whole Dashboard summary.
        }

        return Ok(new DashboardSummaryWithJobsDto(
            summary.ImportantEmailsToday, summary.OpenCases, summary.AwaitingReply, summary.Overdue,
            summary.Escalated, summary.CompletedToday, summary.OnlineEmployees, summary.OfflineEmployees,
            summary.OpenCasesByEmployee, summary.PendingAgentApprovals, summary.AiErrors,
            summary.EmailMonitoringErrors, backgroundJobFailures));
    }
}
