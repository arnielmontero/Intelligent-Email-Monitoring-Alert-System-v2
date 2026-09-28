using Iemas.Application.Audit;
using Iemas.Application.Operations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Iemas.Api.Controllers;

/// <summary>
/// Requirements §91 (Emergency Pause — permission-controlled, audited, timestamped, attributed),
/// §85 ("Emergency controls" permission), §86 (SYSTEM → Maintenance / Emergency Pause).
/// </summary>
[ApiController]
[Route("api/v1/maintenance")]
[Authorize(Policy = "RequireAuditorOrAbove")]
public class MaintenanceController : ControllerBase
{
    private readonly EmergencyPauseService _pauseService;
    private readonly AuditQueryService _auditQueryService;
    private readonly EmailDataResetService _resetService;

    public MaintenanceController(EmergencyPauseService pauseService, AuditQueryService auditQueryService, EmailDataResetService resetService)
    {
        _pauseService = pauseService;
        _auditQueryService = auditQueryService;
        _resetService = resetService;
    }

    [HttpGet("pause")]
    public async Task<ActionResult<List<PauseControlDto>>> GetPauseControls(CancellationToken cancellationToken) =>
        Ok(await _pauseService.GetAllAsync(cancellationToken));

    [HttpPut("pause/{control}")]
    [Authorize(Policy = "RequireAdministrator")]
    public async Task<ActionResult<PauseControlDto>> SetPause(string control, [FromBody] SetPauseRequest request, CancellationToken cancellationToken)
    {
        var result = await _pauseService.SetAsync(control, request, cancellationToken);
        return result.Succeeded ? Ok(result.Value) : BadRequest(new { message = result.Error });
    }

    [HttpGet("pause/history")]
    public async Task<ActionResult<List<AuditLogDto>>> GetPauseHistory([FromQuery] int? take, CancellationToken cancellationToken) =>
        Ok(await _auditQueryService.SearchAsync(null, "EmergencyPause", null, null, take ?? 50, cancellationToken));

    /// <summary>What a reset of email data would remove right now.</summary>
    [HttpGet("reset-email-data")]
    [Authorize(Policy = "RequireSuperAdministrator")]
    public async Task<ActionResult<EmailDataResetPreviewDto>> PreviewEmailDataReset(CancellationToken cancellationToken) =>
        Ok(await _resetService.PreviewAsync(cancellationToken));

    /// <summary>Removes all email-derived data and keeps configuration; the body must carry the confirmation word.</summary>
    [HttpPost("reset-email-data")]
    [Authorize(Policy = "RequireSuperAdministrator")]
    public async Task<ActionResult<EmailDataResetResultDto>> ResetEmailData([FromBody] ResetEmailDataRequest request, CancellationToken cancellationToken)
    {
        var result = await _resetService.ResetAsync(request.Confirmation, request.RemoveTestRecordIds, cancellationToken);
        return result is null
            ? BadRequest(new { message = $"Type {EmailDataResetService.ConfirmationWord} to confirm the reset." })
            : Ok(result);
    }
}

public record ResetEmailDataRequest(string? Confirmation, List<Guid>? RemoveTestRecordIds);
