using Iemas.Application.Escalations;
using Iemas.Application.Escalations.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Iemas.Api.Controllers;

/// <summary>
/// Requirements §63 (audit visibility), §65 (Supervisor Case Access — this endpoint requires
/// normal IEMAS authentication/authorization, never an unauthenticated public Case URL). Manual
/// trigger + read access, mirroring RemindersController's shape; normal operation is the Hangfire
/// recurring job in Program.cs.
/// </summary>
[ApiController]
[Route("api/v1/escalations")]
[Authorize(Policy = "RequireSupervisorOrAbove")]
public class EscalationsController : ControllerBase
{
    private readonly EscalationQueryService _queryService;
    private readonly EscalationService _escalationService;

    public EscalationsController(EscalationQueryService queryService, EscalationService escalationService)
    {
        _queryService = queryService;
        _escalationService = escalationService;
    }

    [HttpGet]
    public async Task<ActionResult<List<EscalationEventDto>>> Search([FromQuery] Guid? caseId, [FromQuery] string? outcome, [FromQuery] int? take, CancellationToken cancellationToken)
    {
        if (caseId is Guid id)
        {
            return Ok(await _queryService.GetForCaseAsync(id, cancellationToken));
        }
        return Ok(await _queryService.SearchAsync(outcome, take ?? 100, cancellationToken));
    }

    [HttpPost("run")]
    [Authorize(Policy = "RequireAdministrator")]
    public async Task<ActionResult<EscalationRunResult>> Run([FromQuery] int? batchSize, CancellationToken cancellationToken)
    {
        return Ok(await _escalationService.RunAsync(batchSize ?? 25, cancellationToken));
    }
}
