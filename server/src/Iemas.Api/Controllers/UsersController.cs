using Iemas.Application.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Iemas.Api.Controllers;

/// <summary>Requirements §85 (RBAC — User management), §86 (PEOPLE &amp; DEVICES → Users &amp; Permissions).</summary>
[ApiController]
[Route("api/v1/users")]
[Authorize]
public class UsersController : ControllerBase
{
    private readonly UserManagementService _service;

    public UsersController(UserManagementService service)
    {
        _service = service;
    }

    [Authorize(Policy = "RequireAdministrator")]
    [HttpGet]
    public async Task<ActionResult<List<UserDto>>> List(CancellationToken cancellationToken) =>
        Ok(await _service.ListAsync(cancellationToken));

    [Authorize(Policy = "RequireAdministrator")]
    [HttpGet("roles")]
    public ActionResult<IReadOnlyList<RoleDto>> Roles() => Ok(UserManagementService.RoleCatalog);

    [Authorize(Policy = "RequireAdministrator")]
    [HttpPost]
    public async Task<ActionResult<UserDto>> Create([FromBody] CreateUserRequest request, CancellationToken cancellationToken)
    {
        var result = await _service.CreateAsync(request, cancellationToken);
        return result.Succeeded ? Ok(result.Value) : BadRequest(new { message = result.Error });
    }

    [Authorize(Policy = "RequireAdministrator")]
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<UserDto>> Update(Guid id, [FromBody] UpdateUserRequest request, CancellationToken cancellationToken) =>
        ToResponse(await _service.UpdateAsync(id, request, cancellationToken));

    [Authorize(Policy = "RequireAdministrator")]
    [HttpPost("{id:guid}/deactivate")]
    public async Task<ActionResult<UserDto>> Deactivate(Guid id, CancellationToken cancellationToken) =>
        ToResponse(await _service.SetActiveAsync(id, false, cancellationToken));

    [Authorize(Policy = "RequireAdministrator")]
    [HttpPost("{id:guid}/activate")]
    public async Task<ActionResult<UserDto>> Activate(Guid id, CancellationToken cancellationToken) =>
        ToResponse(await _service.SetActiveAsync(id, true, cancellationToken));

    [Authorize(Policy = "RequireAdministrator")]
    [HttpPost("{id:guid}/reset-password")]
    public async Task<IActionResult> ResetPassword(Guid id, [FromBody] ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        var result = await _service.ResetPasswordAsync(id, request, cancellationToken);
        if (result.Succeeded) return NoContent();
        return result.Error == "User not found." ? NotFound(new { message = result.Error }) : BadRequest(new { message = result.Error });
    }

    /// <summary>Any signed-in CMS user may change their own password. Rate-limited: it verifies a password.</summary>
    [HttpPost("me/password")]
    [EnableRateLimiting("AuthRateLimit")]
    public async Task<IActionResult> ChangeOwnPassword([FromBody] ChangeOwnPasswordRequest request, CancellationToken cancellationToken)
    {
        var result = await _service.ChangeOwnPasswordAsync(request, cancellationToken);
        return result.Succeeded ? NoContent() : BadRequest(new { message = result.Error });
    }

    private ActionResult<UserDto> ToResponse(Application.Common.Result<UserDto> result)
    {
        if (result.Succeeded) return Ok(result.Value);
        return result.Error == "User not found." ? NotFound(new { message = result.Error }) : BadRequest(new { message = result.Error });
    }
}
