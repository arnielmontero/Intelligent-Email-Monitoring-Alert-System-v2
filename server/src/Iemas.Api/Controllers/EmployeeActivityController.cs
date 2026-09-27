using Iemas.Application.EmployeeActivity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Iemas.Api.Controllers;

/// <summary>Requirements §67 (Employee Activity — what did the employee do?), §86 (PEOPLE &amp; DEVICES → Employee Activity). Read-only.</summary>
[ApiController]
[Route("api/v1/employee-activity")]
[Authorize(Policy = "RequireAuditorOrAbove")]
public class EmployeeActivityController : ControllerBase
{
    private readonly EmployeeActivityQueryService _service;

    public EmployeeActivityController(EmployeeActivityQueryService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<List<EmployeeActivityDto>>> Search(
        [FromQuery] Guid? employeeId,
        [FromQuery] Guid? caseId,
        [FromQuery] string? activity,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int? take,
        CancellationToken cancellationToken) =>
        Ok(await _service.SearchAsync(employeeId, caseId, activity, from, to, take ?? 100, cancellationToken));

    /// <summary>Defaults to the last 7 days.</summary>
    [HttpGet("summary")]
    public async Task<ActionResult<List<EmployeeActivitySummaryDto>>> Summary(
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, CancellationToken cancellationToken)
    {
        var toValue = to ?? DateTimeOffset.UtcNow;
        var fromValue = from ?? toValue.AddDays(-7);
        if (fromValue > toValue) return BadRequest(new { message = "'from' must be before 'to'." });
        return Ok(await _service.GetSummaryAsync(fromValue, toValue, cancellationToken));
    }
}
