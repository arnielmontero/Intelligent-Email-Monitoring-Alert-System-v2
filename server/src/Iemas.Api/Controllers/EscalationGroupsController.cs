using Iemas.Application.Escalations;
using Iemas.Application.Escalations.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Iemas.Api.Controllers;

/// <summary>§59 "Specific Group" recipient type — CRUD for escalation groups.</summary>
[ApiController]
[Route("api/v1/escalation-groups")]
[Authorize(Policy = "RequireAdministrator")]
public class EscalationGroupsController : ControllerBase
{
    private readonly EscalationGroupService _service;

    public EscalationGroupsController(EscalationGroupService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<List<EscalationGroupDto>>> GetAll(CancellationToken cancellationToken)
    {
        return Ok(await _service.GetAllAsync(cancellationToken));
    }

    [HttpPost]
    public async Task<ActionResult<EscalationGroupDto>> Create([FromBody] SaveEscalationGroupRequest request, CancellationToken cancellationToken)
    {
        var result = await _service.CreateAsync(request, cancellationToken);
        return result.Succeeded ? Ok(result.Value) : BadRequest(new { message = result.Error });
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<EscalationGroupDto>> Update(Guid id, [FromBody] SaveEscalationGroupRequest request, CancellationToken cancellationToken)
    {
        var result = await _service.UpdateAsync(id, request, cancellationToken);
        if (!result.Succeeded)
        {
            return result.Error == "Group not found." ? NotFound(new { message = result.Error }) : BadRequest(new { message = result.Error });
        }
        return Ok(result.Value);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await _service.DeleteAsync(id, cancellationToken);
        if (!result.Succeeded)
        {
            return result.Error == "Group not found." ? NotFound(new { message = result.Error }) : BadRequest(new { message = result.Error });
        }
        return NoContent();
    }
}
