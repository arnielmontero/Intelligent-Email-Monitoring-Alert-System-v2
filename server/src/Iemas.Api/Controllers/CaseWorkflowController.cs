using Iemas.Application.Cases;
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
}
