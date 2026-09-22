using Iemas.Application.EmailIntake;
using Iemas.Application.EmailIntake.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Iemas.Api.Controllers;

/// <summary>
/// Manual trigger for the intake pipeline (§20). Normal operation is driven by the Hangfire
/// recurring job registered in Program.cs; this endpoint exists for admin-initiated re-sync
/// and for verification/testing, so intake can be exercised on demand without waiting for the
/// schedule.
/// </summary>
[ApiController]
[Route("api/v1/email-intake")]
[Authorize(Policy = "RequireAdministrator")]
public class EmailIntakeController : ControllerBase
{
    private readonly EmailIntakeService _intakeService;

    public EmailIntakeController(EmailIntakeService intakeService)
    {
        _intakeService = intakeService;
    }

    [HttpPost("run")]
    public async Task<ActionResult<List<IntakeRunResult>>> RunAll(CancellationToken cancellationToken)
    {
        return Ok(await _intakeService.RunAllAsync(cancellationToken));
    }

    [HttpPost("accounts/{emailAccountId:guid}/run")]
    public async Task<ActionResult<IntakeRunResult>> RunForAccount(Guid emailAccountId, CancellationToken cancellationToken)
    {
        return Ok(await _intakeService.RunForAccountAsync(emailAccountId, cancellationToken));
    }
}
