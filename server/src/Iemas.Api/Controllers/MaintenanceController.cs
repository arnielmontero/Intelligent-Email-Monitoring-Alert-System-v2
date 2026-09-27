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

    public MaintenanceController(EmergencyPauseService pauseService, AuditQueryService auditQueryService)
    {
        _pauseService = pauseService;
        _auditQueryService = auditQueryService;
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
}
