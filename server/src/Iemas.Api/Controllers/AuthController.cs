using Iemas.Application.Auth;
using Iemas.Application.Auth.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Iemas.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public class AuthController : ControllerBase
{
    private readonly AuthService _authService;

    public AuthController(AuthService authService)
    {
        _authService = authService;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("AuthRateLimit")]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await _authService.LoginAsync(request, cancellationToken);
        if (result is null)
        {
            return Unauthorized(new { message = "Invalid email or password." });
        }

        return Ok(result);
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<ActionResult<LoginResponse>> Refresh([FromBody] RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        var result = await _authService.RefreshAsync(request, cancellationToken);
        if (result is null)
        {
            return Unauthorized(new { message = "Invalid or expired refresh token." });
        }

        return Ok(result);
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout([FromBody] RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        await _authService.RevokeAsync(request.RefreshToken, cancellationToken);
        return NoContent();
    }
}
