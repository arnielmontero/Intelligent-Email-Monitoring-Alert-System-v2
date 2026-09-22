using Iemas.Application.Agents;
using Iemas.Application.Agents.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Iemas.Api.Controllers;

/// <summary>
/// Requirements §68 — the unauthenticated enrollment surface: a freshly-installed Agent has no
/// credential yet, so registration and status-polling must be reachable without a bearer token.
/// Security relies on the opaque, unguessable RegistrationRequestToken (§68's own diagram implies
/// this — the Agent cannot present a credential it doesn't have yet), not on RBAC.
/// </summary>
[ApiController]
[Route("api/v1/agent-enrollment")]
[AllowAnonymous]
public class AgentEnrollmentController : ControllerBase
{
    private readonly AgentRegistrationService _service;

    public AgentEnrollmentController(AgentRegistrationService service)
    {
        _service = service;
    }

    [HttpPost("register")]
    public async Task<ActionResult<RegisterAgentResponse>> Register([FromBody] RegisterAgentRequest request, CancellationToken cancellationToken)
    {
        var result = await _service.RegisterAsync(request, HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
        return result.Succeeded ? Ok(result.Value) : BadRequest(new { message = result.Error });
    }

    /// <summary>§68 — the Agent polls this with the token it received from Register, never with the Agent ID alone.</summary>
    [HttpGet("status/{registrationRequestToken}")]
    public async Task<ActionResult<AgentRegistrationStatusResponse>> GetStatus(string registrationRequestToken, CancellationToken cancellationToken)
    {
        var result = await _service.GetRegistrationStatusAsync(registrationRequestToken, cancellationToken);
        return result.Succeeded ? Ok(result.Value) : NotFound(new { message = result.Error });
    }
}
