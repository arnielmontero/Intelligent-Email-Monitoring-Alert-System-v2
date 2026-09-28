using Iemas.Application.Cases;
using Iemas.Application.Cases.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Iemas.Api.Controllers;

/// <summary>
/// Manual trigger for Reply Verification, mirroring EmailIntakeController/CaseWorkflowController —
/// normal operation is the Hangfire recurring job in Program.cs.
/// </summary>
[ApiController]
[Route("api/v1/reply-verification")]
[Authorize(Policy = "RequireAdministrator")]
public class ReplyVerificationController : ControllerBase
{
    private readonly ReplyVerificationService _service;

    public ReplyVerificationController(ReplyVerificationService service)
    {
        _service = service;
    }

    [HttpPost("run")]
    public async Task<ActionResult<ReplyVerificationRunResult>> Run([FromQuery] int? batchSize, CancellationToken cancellationToken)
    {
        return Ok(await _service.RunAsync(batchSize ?? 25, cancellationToken));
    }

    [HttpGet("overview")]
    public async Task<ActionResult<ReplyCheckOverviewDto>> Overview(CancellationToken cancellationToken) =>
        Ok(await _service.GetOverviewAsync(cancellationToken));

    [HttpGet("cases")]
    public async Task<ActionResult<List<ReplyCheckCaseDto>>> Cases([FromQuery] Iemas.Domain.Cases.CaseReplyStatus? status, [FromQuery] int? take, CancellationToken cancellationToken) =>
        Ok(await _service.GetCasesAsync(status, take ?? 200, cancellationToken));
}
