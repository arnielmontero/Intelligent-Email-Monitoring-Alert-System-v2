using Iemas.Application.Reminders;
using Iemas.Application.Reminders.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Iemas.Api.Controllers;

/// <summary>
/// Requirements §54-§56 — administrative visibility into Reminders (read, manual cancel/reschedule)
/// and a manual trigger for the execution job, mirroring CaseWorkflowController/
/// EmailIntakeController's manual-run pattern. Normal operation is the Hangfire recurring job in
/// Program.cs; this controller exists for CMS visibility and administrative override, not as the
/// primary execution path.
/// </summary>
[ApiController]
[Route("api/v1/reminders")]
[Authorize(Policy = "RequireSupervisorOrAbove")]
public class RemindersController : ControllerBase
{
    private readonly ReminderQueryService _queryService;
    private readonly ReminderExecutionService _executionService;

    public RemindersController(ReminderQueryService queryService, ReminderExecutionService executionService)
    {
        _queryService = queryService;
        _executionService = executionService;
    }

    [HttpGet]
    public async Task<ActionResult<List<ReminderDto>>> Search([FromQuery] Guid? caseId, [FromQuery] string? status, [FromQuery] int? take, CancellationToken cancellationToken)
    {
        if (caseId is Guid id)
        {
            return Ok(await _queryService.GetForCaseAsync(id, cancellationToken));
        }
        return Ok(await _queryService.SearchAsync(status, take ?? 100, cancellationToken));
    }

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = "RequireAdministrator")]
    public async Task<IActionResult> Cancel(Guid id, [FromBody] CancelReminderRequest? request, CancellationToken cancellationToken)
    {
        var ok = await _executionService.CancelAsync(id, request?.Reason, cancellationToken);
        return ok ? NoContent() : NotFound();
    }

    [HttpPost("{id:guid}/reschedule")]
    [Authorize(Policy = "RequireAdministrator")]
    public async Task<IActionResult> Reschedule(Guid id, [FromBody] RescheduleReminderRequest request, CancellationToken cancellationToken)
    {
        var ok = await _executionService.RescheduleAsync(id, request.NewScheduledForUtc, cancellationToken);
        return ok ? NoContent() : NotFound();
    }

    [HttpPost("run")]
    [Authorize(Policy = "RequireAdministrator")]
    public async Task<ActionResult<ReminderRunResult>> Run([FromQuery] int? batchSize, CancellationToken cancellationToken)
    {
        return Ok(await _executionService.RunAsync(batchSize ?? 25, cancellationToken));
    }
}

public record CancelReminderRequest(string? Reason);
public record RescheduleReminderRequest(DateTimeOffset NewScheduledForUtc);
