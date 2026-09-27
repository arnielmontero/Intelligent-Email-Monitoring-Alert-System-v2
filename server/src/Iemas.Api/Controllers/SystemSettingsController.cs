using Iemas.Application.Operations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Iemas.Api.Controllers;

/// <summary>Requirements §86 (SYSTEM → System Settings), §90 (retention configuration). Every change is audited.</summary>
[ApiController]
[Route("api/v1/system-settings")]
[Authorize]
public class SystemSettingsController : ControllerBase
{
    private readonly SystemSettingsService _service;

    public SystemSettingsController(SystemSettingsService service)
    {
        _service = service;
    }

    [Authorize(Policy = "RequireAdministrator")]
    [HttpGet]
    public async Task<ActionResult<List<SystemSettingDto>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await _service.GetAllAsync(cancellationToken));

    /// <summary>Non-sensitive values any signed-in CMS user needs (e.g. the header's organization name).</summary>
    [HttpGet("public")]
    public async Task<ActionResult<PublicSystemSettingsDto>> GetPublic(CancellationToken cancellationToken) =>
        Ok(await _service.GetPublicAsync(cancellationToken));

    [Authorize(Policy = "RequireAdministrator")]
    [HttpPut]
    public async Task<ActionResult<List<SystemSettingDto>>> Update([FromBody] UpdateSystemSettingsRequest request, CancellationToken cancellationToken)
    {
        var result = await _service.UpdateAsync(request, cancellationToken);
        return result.Succeeded ? Ok(result.Value) : BadRequest(new { message = result.Error });
    }

    [Authorize(Policy = "RequireAdministrator")]
    [HttpDelete("{key}")]
    public async Task<IActionResult> Reset(string key, CancellationToken cancellationToken)
    {
        var result = await _service.ResetAsync(key, cancellationToken);
        return result.Succeeded ? NoContent() : BadRequest(new { message = result.Error });
    }
}
