using Iemas.Application.Agents;
using Iemas.Application.Agents.Dtos;
using Iemas.Domain.Agents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Iemas.Api.Controllers;

/// <summary>Requirements §71 — CMS administration of registered Agents (list, approve, reject, revoke, logs).</summary>
[ApiController]
[Route("api/v1/agents")]
[Authorize(Policy = "RequireAdministrator")]
public class AgentsController : ControllerBase
{
    private readonly AgentManagementService _managementService;
    private readonly AgentRegistrationService _registrationService;

    public AgentsController(AgentManagementService managementService, AgentRegistrationService registrationService)
    {
        _managementService = managementService;
        _registrationService = registrationService;
    }

    [HttpGet]
    public async Task<ActionResult<List<AgentDto>>> GetAll([FromQuery] AgentRegistrationStatus? status, CancellationToken cancellationToken)
    {
        return Ok(await _managementService.GetAllAsync(status, cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AgentDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var agent = await _managementService.GetByIdAsync(id, cancellationToken);
        return agent is null ? NotFound() : Ok(agent);
    }

    [HttpGet("{id:guid}/logs")]
    public async Task<ActionResult<List<AgentLogDto>>> GetLogs(Guid id, CancellationToken cancellationToken)
    {
        return Ok(await _managementService.GetLogsAsync(id, cancellationToken));
    }

    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, [FromBody] ApproveAgentRequest request, CancellationToken cancellationToken)
    {
        var approvedByUserId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        var result = await _registrationService.ApproveAsync(id, request, approvedByUserId, cancellationToken);
        if (!result.Succeeded)
        {
            return result.Error == "Agent registration not found." ? NotFound(new { message = result.Error }) : BadRequest(new { message = result.Error });
        }

        return NoContent();
    }

    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, [FromBody] RejectAgentRequest request, CancellationToken cancellationToken)
    {
        var result = await _registrationService.RejectAsync(id, request, cancellationToken);
        if (!result.Succeeded)
        {
            return result.Error == "Agent registration not found." ? NotFound(new { message = result.Error }) : BadRequest(new { message = result.Error });
        }

        return NoContent();
    }

    [HttpPost("{id:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id, [FromBody] RevokeAgentRequest request, CancellationToken cancellationToken)
    {
        var result = await _registrationService.RevokeAsync(id, request, cancellationToken);
        if (!result.Succeeded)
        {
            return result.Error == "Agent registration not found." ? NotFound(new { message = result.Error }) : BadRequest(new { message = result.Error });
        }

        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await _managementService.DeleteAsync(id, cancellationToken);
        if (!result.Succeeded)
        {
            return result.Error == "Agent registration not found." ? NotFound(new { message = result.Error }) : BadRequest(new { message = result.Error });
        }

        return NoContent();
    }
}
