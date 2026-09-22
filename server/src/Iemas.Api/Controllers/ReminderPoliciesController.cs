using Iemas.Application.Reminders;
using Iemas.Application.Reminders.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Iemas.Api.Controllers;

/// <summary>Requirements §54 CMS "Reminder Policies" — configuration CRUD, mirroring AiModelsController's shape.</summary>
[ApiController]
[Route("api/v1/reminder-policies")]
[Authorize(Policy = "RequireAdministrator")]
public class ReminderPoliciesController : ControllerBase
{
    private readonly ReminderPolicyService _service;

    public ReminderPoliciesController(ReminderPolicyService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<List<ReminderPolicyDto>>> GetAll(CancellationToken cancellationToken)
    {
        return Ok(await _service.GetAllAsync(cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ReminderPolicyDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var policy = await _service.GetByIdAsync(id, cancellationToken);
        return policy is null ? NotFound() : Ok(policy);
    }

    [HttpPost]
    public async Task<ActionResult<ReminderPolicyDto>> Create([FromBody] SaveReminderPolicyRequest request, CancellationToken cancellationToken)
    {
        var result = await _service.CreateAsync(request, cancellationToken);
        return result.Succeeded ? Ok(result.Value) : BadRequest(new { message = result.Error });
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ReminderPolicyDto>> Update(Guid id, [FromBody] SaveReminderPolicyRequest request, CancellationToken cancellationToken)
    {
        var result = await _service.UpdateAsync(id, request, cancellationToken);
        if (!result.Succeeded)
        {
            return result.Error == "Reminder Policy not found." ? NotFound(new { message = result.Error }) : BadRequest(new { message = result.Error });
        }
        return Ok(result.Value);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await _service.DeleteAsync(id, cancellationToken);
        if (!result.Succeeded)
        {
            return result.Error == "Reminder Policy not found." ? NotFound(new { message = result.Error }) : BadRequest(new { message = result.Error });
        }
        return NoContent();
    }
}
