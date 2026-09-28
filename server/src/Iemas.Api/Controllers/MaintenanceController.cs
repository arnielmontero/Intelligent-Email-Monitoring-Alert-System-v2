using Iemas.Application.Audit;
using Iemas.Application.Cases;
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
    private readonly SampleDataService _sampleDataService;
    private readonly CaseWorkflowService _caseWorkflowService;

    public MaintenanceController(EmergencyPauseService pauseService, AuditQueryService auditQueryService, EmailDataResetService resetService,
        SampleDataService sampleDataService, CaseWorkflowService caseWorkflowService)
    {
        _pauseService = pauseService;
        _auditQueryService = auditQueryService;
        _resetService = resetService;
        _sampleDataService = sampleDataService;
        _caseWorkflowService = caseWorkflowService;
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

    /// <summary>
    /// Adds sample customer emails to a mailbox for trying the system. Pre-classified samples are turned into Cases
    /// straight away (the same Case step that runs after classification and every 2 minutes); AI samples wait for the classifier.
    /// </summary>
    [HttpPost("sample-data")]
    [Authorize(Policy = "RequireSuperAdministrator")]
    public async Task<ActionResult<SampleDataRunResult>> GenerateSampleData([FromBody] GenerateSampleDataRequest request, CancellationToken cancellationToken)
    {
        var (result, error) = await _sampleDataService.GenerateAsync(request, cancellationToken);
        if (result is null) return BadRequest(new { message = error });
        var caseRun = request.UseAi ? null : await _caseWorkflowService.RunAsync(100, cancellationToken);
        return Ok(new SampleDataRunResult(result, caseRun));
    }
}

public record SampleDataRunResult(GenerateSampleDataResult Samples, CaseRunResult? CaseRun);

public record ResetEmailDataRequest(string? Confirmation, List<Guid>? RemoveTestRecordIds);
