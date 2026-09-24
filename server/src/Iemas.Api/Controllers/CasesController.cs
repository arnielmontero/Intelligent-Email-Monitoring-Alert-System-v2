using Iemas.Application.Cases;
using Iemas.Application.Cases.Dtos;
using Iemas.Domain.Cases;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Iemas.Api.Controllers;

/// <summary>Requirements §88 (Search), §89 (Case Investigation), §48 (Completion).</summary>
[ApiController]
[Route("api/v1/cases")]
[Authorize(Policy = "RequireSupervisorOrAbove")]
public class CasesController : ControllerBase
{
    private readonly CaseService _caseService;
    private readonly CaseWorkflowService _workflowService;

    public CasesController(CaseService caseService, CaseWorkflowService workflowService)
    {
        _caseService = caseService;
        _workflowService = workflowService;
    }

    [HttpGet]
    public async Task<ActionResult<List<CaseDto>>> Search(
        [FromQuery] CaseWorkStatus? workStatus,
        [FromQuery] Guid? ownerEmployeeId,
        [FromQuery] Guid? emailAccountId,
        [FromQuery] string? customerEmailAddress,
        [FromQuery] string? search,
        CancellationToken cancellationToken)
    {
        var filter = new CaseListFilter(workStatus, ownerEmployeeId, emailAccountId, customerEmailAddress, search);
        return Ok(await _caseService.SearchAsync(filter, cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CaseDetailDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var detail = await _caseService.GetDetailAsync(id, cancellationToken);
        return detail is null ? NotFound() : Ok(detail);
    }

    /// <summary>§66/§86 "Case History & Logs" — global, cross-Case search, distinct from GetById's single-Case timeline.</summary>
    [HttpGet("events")]
    public async Task<ActionResult<List<CaseEventSearchResultDto>>> SearchEvents(
        [FromQuery] Guid? caseId, [FromQuery] CaseEventType? eventType,
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] int? take, CancellationToken cancellationToken)
    {
        return Ok(await _caseService.SearchEventsAsync(caseId, eventType, from, to, take ?? 100, cancellationToken));
    }

    [HttpPost("{id:guid}/complete")]
    [Authorize(Policy = "RequireAdministrator")]
    public async Task<IActionResult> Complete(Guid id, [FromBody] CompleteCaseRequest request, CancellationToken cancellationToken)
    {
        var result = await _workflowService.CompleteAsync(id, request, cancellationToken);
        if (!result.Succeeded)
        {
            return result.Error == "Case not found." ? NotFound(new { message = result.Error }) : BadRequest(new { message = result.Error });
        }

        return NoContent();
    }
}
