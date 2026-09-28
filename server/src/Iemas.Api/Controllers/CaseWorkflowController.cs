using Iemas.Application.Cases;
using Iemas.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Iemas.Api.Controllers;

/// <summary>
/// Manual trigger for Case matching/creation, mirroring EmailIntakeController/
/// EmailClassificationController — normal operation is the Hangfire recurring job in Program.cs.
/// </summary>
[ApiController]
[Route("api/v1/case-workflow")]
[Authorize(Policy = "RequireAdministrator")]
public class CaseWorkflowController : ControllerBase
{
    private readonly CaseWorkflowService _service;

    public CaseWorkflowController(CaseWorkflowService service)
    {
        _service = service;
    }

    [HttpPost("run")]
    public async Task<ActionResult<CaseRunResult>> Run([FromQuery] int? batchSize, CancellationToken cancellationToken)
    {
        return Ok(await _service.RunAsync(batchSize ?? 25, cancellationToken));
    }

    [HttpGet("overview")]
    public async Task<ActionResult<CaseWorkflowOverviewDto>> Overview(CancellationToken cancellationToken) =>
        Ok(await _service.GetOverviewAsync(cancellationToken));

    [HttpGet("waiting")]
    public async Task<ActionResult<List<CaseWorkflowItemDto>>> Waiting([FromQuery] int? take, CancellationToken cancellationToken) =>
        Ok(await _service.GetWaitingAsync(take ?? 50, cancellationToken));

    [HttpGet("recent")]
    public async Task<ActionResult<PagedResult<CaseWorkflowItemDto>>> Recent(
        [FromQuery] int? page, [FromQuery] int? pageSize, [FromQuery] string? search, [FromQuery] string? sort, [FromQuery] bool? desc,
        CancellationToken cancellationToken) =>
        Ok(await _service.GetRecentAsync(page ?? 1, pageSize ?? 10, search, sort, desc ?? true, cancellationToken));
}
