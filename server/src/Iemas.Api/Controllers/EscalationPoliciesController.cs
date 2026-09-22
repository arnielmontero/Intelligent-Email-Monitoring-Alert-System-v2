using Iemas.Application.Escalations;
using Iemas.Application.Escalations.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Iemas.Api.Controllers;

/// <summary>Requirements §57 CMS "Escalation Policies" — configuration CRUD + Test Policy, mirroring ReminderPoliciesController's shape.</summary>
[ApiController]
[Route("api/v1/escalation-policies")]
[Authorize(Policy = "RequireAdministrator")]
public class EscalationPoliciesController : ControllerBase
{
    private readonly EscalationPolicyService _service;

    public EscalationPoliciesController(EscalationPolicyService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<List<EscalationPolicyDto>>> GetAll(CancellationToken cancellationToken)
    {
        return Ok(await _service.GetAllAsync(cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<EscalationPolicyDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var policy = await _service.GetByIdAsync(id, cancellationToken);
        return policy is null ? NotFound() : Ok(policy);
    }

    [HttpPost]
    public async Task<ActionResult<EscalationPolicyDto>> Create([FromBody] SaveEscalationPolicyRequest request, CancellationToken cancellationToken)
    {
        var result = await _service.CreateAsync(request, cancellationToken);
        return result.Succeeded ? Ok(result.Value) : BadRequest(new { message = result.Error });
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<EscalationPolicyDto>> Update(Guid id, [FromBody] SaveEscalationPolicyRequest request, CancellationToken cancellationToken)
    {
        var result = await _service.UpdateAsync(id, request, cancellationToken);
        if (!result.Succeeded)
        {
            return result.Error == "Escalation Policy not found." ? NotFound(new { message = result.Error }) : BadRequest(new { message = result.Error });
        }
        return Ok(result.Value);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await _service.DeleteAsync(id, cancellationToken);
        if (!result.Succeeded)
        {
            return result.Error == "Escalation Policy not found." ? NotFound(new { message = result.Error }) : BadRequest(new { message = result.Error });
        }
        return NoContent();
    }

    /// <summary>§57 "Test Policy" — dry-run against a real Case, no state written.</summary>
    [HttpPost("{id:guid}/test")]
    public async Task<ActionResult<TestEscalationPolicyResult>> Test(Guid id, [FromQuery] Guid caseId, CancellationToken cancellationToken)
    {
        var result = await _service.TestAsync(id, caseId, cancellationToken);
        return Ok(result.Value);
    }
}
