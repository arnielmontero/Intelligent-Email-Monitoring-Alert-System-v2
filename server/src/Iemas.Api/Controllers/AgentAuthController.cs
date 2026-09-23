using Iemas.Application.Agents;
using Iemas.Application.Agents.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Iemas.Api.Controllers;

/// <summary>Requirements §68 "Agent Authenticates → CONNECTED", §70 (rotation).</summary>
[ApiController]
[Route("api/v1/agent-auth")]
public class AgentAuthController : ControllerBase
{
    private readonly AgentAuthService _service;

    public AgentAuthController(AgentAuthService service)
    {
        _service = service;
    }

    [HttpPost("authenticate")]
    [AllowAnonymous]
    [EnableRateLimiting("AuthRateLimit")]
    public async Task<ActionResult<AgentAuthenticateResponse>> Authenticate([FromBody] AgentAuthenticateRequest request, CancellationToken cancellationToken)
    {
        var result = await _service.AuthenticateAsync(request, HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
        // Deliberately uniform 401 for every failure mode (unknown Agent, wrong key, revoked,
        // expired, not-yet-approved) — never reveals which one, so an attacker cannot enumerate
        // valid Agent IDs or distinguish "wrong key" from "not approved yet."
        return result.Succeeded ? Ok(result.Value) : Unauthorized(new { message = result.Error });
    }

    [HttpPost("rotate")]
    [Authorize(Policy = "RequireAgent")]
    public async Task<ActionResult<RotateAgentCredentialResponse>> Rotate(CancellationToken cancellationToken)
    {
        var agentId = Guid.Parse(User.FindFirst("agent_id")!.Value);
        var result = await _service.RotateCredentialAsync(agentId, cancellationToken);
        return result.Succeeded ? Ok(result.Value) : BadRequest(new { message = result.Error });
    }
}
