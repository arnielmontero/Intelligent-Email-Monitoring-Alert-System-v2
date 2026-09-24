using Iemas.Application.Agents;
using Iemas.Application.Agents.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

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
    [EnableRateLimiting("AuthRateLimit")]
    public async Task<ActionResult<RegisterAgentResponse>> Register([FromBody] RegisterAgentRequest request, CancellationToken cancellationToken)
    {
        var result = await _service.RegisterAsync(request, HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
        return result.Succeeded ? Ok(result.Value) : BadRequest(new { message = result.Error });
    }

    /// <summary>
    /// §68 — the Agent polls this with the token it received from Register, never with the Agent ID
    /// alone. Rate-limited (found missing during this session's §84-85 security re-verification
    /// pass — this is an unauthenticated, token-guessing-adjacent surface, same category as
    /// Register/Authenticate below it): without a limit, an attacker could brute-force-guess a
    /// valid RegistrationRequestToken with unlimited attempts. Uses the more permissive
    /// AgentPollRateLimit (60/min), not the strict 10/min AuthRateLimit, since the Windows Agent
    /// itself legitimately polls this every ~5s (12/min) while PendingApproval.
    /// </summary>
    [HttpGet("status/{registrationRequestToken}")]
    [EnableRateLimiting("AgentPollRateLimit")]
    public async Task<ActionResult<AgentRegistrationStatusResponse>> GetStatus(string registrationRequestToken, CancellationToken cancellationToken)
    {
        var result = await _service.GetRegistrationStatusAsync(registrationRequestToken, cancellationToken);
        return result.Succeeded ? Ok(result.Value) : NotFound(new { message = result.Error });
    }
}
