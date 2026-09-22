using Iemas.Application.EmailClassification;
using Iemas.Application.EmailClassification.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Iemas.Api.Controllers;

/// <summary>
/// Manual trigger for the classification pipeline, mirroring EmailIntakeController — normal
/// operation is the Hangfire recurring job in Program.cs; this exists for admin-initiated
/// re-classification and verification/testing on demand.
/// </summary>
[ApiController]
[Route("api/v1/email-classification")]
[Authorize(Policy = "RequireAdministrator")]
public class EmailClassificationController : ControllerBase
{
    private readonly EmailClassificationService _service;

    public EmailClassificationController(EmailClassificationService service)
    {
        _service = service;
    }

    [HttpPost("run")]
    public async Task<ActionResult<ClassificationRunResult>> Run([FromQuery] int? batchSize, CancellationToken cancellationToken)
    {
        return Ok(await _service.RunAsync(batchSize ?? 25, cancellationToken));
    }

    [HttpPost("messages/{emailMessageId:guid}/run")]
    public async Task<IActionResult> RunForMessage(Guid emailMessageId, CancellationToken cancellationToken)
    {
        var decision = await _service.ClassifyOneAsync(emailMessageId, cancellationToken);
        return decision is null ? NotFound(new { message = "Message not found or no longer pending classification." }) : Ok(new { decision = decision.ToString() });
    }
}
