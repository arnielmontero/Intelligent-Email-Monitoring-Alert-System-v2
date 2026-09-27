using Iemas.Application.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Iemas.Api.Controllers;

/// <summary>Requirements §51 (editable notification templates), §104 (delivery tracking), §86 (CASE MANAGEMENT → Notifications).</summary>
[ApiController]
[Route("api/v1/notifications")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly NotificationAdminService _service;

    public NotificationsController(NotificationAdminService service)
    {
        _service = service;
    }

    [Authorize(Policy = "RequireSupervisorOrAbove")]
    [HttpGet]
    public async Task<ActionResult<List<NotificationDto>>> Search(
        [FromQuery] Guid? employeeId,
        [FromQuery] Guid? caseId,
        [FromQuery] string? status,
        [FromQuery] string? type,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int? take,
        CancellationToken cancellationToken) =>
        Ok(await _service.SearchAsync(employeeId, caseId, status, type, from, to, take ?? 100, cancellationToken));

    [Authorize(Policy = "RequireAdministrator")]
    [HttpGet("templates")]
    public async Task<ActionResult<NotificationTemplatesResponse>> GetTemplates(CancellationToken cancellationToken) =>
        Ok(await _service.GetTemplatesAsync(cancellationToken));

    [Authorize(Policy = "RequireAdministrator")]
    [HttpPut("templates/{type}")]
    public async Task<ActionResult<NotificationTemplateDto>> UpdateTemplate(string type, [FromBody] UpdateNotificationTemplateRequest request, CancellationToken cancellationToken)
    {
        var result = await _service.UpdateTemplateAsync(type, request, cancellationToken);
        return result.Succeeded ? Ok(result.Value) : BadRequest(new { message = result.Error });
    }

    [Authorize(Policy = "RequireAdministrator")]
    [HttpPost("templates/{type}/reset")]
    public async Task<ActionResult<NotificationTemplateDto>> ResetTemplate(string type, CancellationToken cancellationToken)
    {
        var result = await _service.ResetTemplateAsync(type, cancellationToken);
        return result.Succeeded ? Ok(result.Value) : BadRequest(new { message = result.Error });
    }

    [Authorize(Policy = "RequireAdministrator")]
    [HttpPost("templates/preview")]
    public ActionResult<NotificationTemplatePreviewDto> Preview([FromBody] UpdateNotificationTemplateRequest request)
    {
        var result = _service.Preview(request);
        return result.Succeeded ? Ok(result.Value) : BadRequest(new { message = result.Error });
    }
}
