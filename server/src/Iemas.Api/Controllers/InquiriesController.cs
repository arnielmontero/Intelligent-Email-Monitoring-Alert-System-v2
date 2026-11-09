using Iemas.Application.Common;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Inquiries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Iemas.Api.Controllers;

public sealed record ResolutionDecisionRequest(string? Comment);
public sealed record ReassignInquiryRequest(Guid EmployeeId);

/// <summary>
/// Inquiry supervision for management (section 7): who has which inquiries in which stage, the
/// full conversation of each, and the actions management can take. Restricted to Supervisor/Manager
/// and above - the conversation includes decrypted agent replies (Q4 "restricted access").
/// </summary>
[ApiController]
[Route("api/v1/inquiries")]
[Authorize(Policy = "RequireSupervisorOrAbove")]
public class InquiriesController : ControllerBase
{
    private readonly InquiryOverviewService _overview;
    private readonly InquiryConversationService _conversation;
    private readonly InquirySupervisionService _supervision;
    private readonly ConversationSyncService _sync;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditService _audit;

    public InquiriesController(
        InquiryOverviewService overview, InquiryConversationService conversation, InquirySupervisionService supervision,
        ConversationSyncService sync, ICurrentUserService currentUser, IAuditService audit)
    {
        _overview = overview;
        _conversation = conversation;
        _supervision = supervision;
        _sync = sync;
        _currentUser = currentUser;
        _audit = audit;
    }

    [HttpGet("overview")]
    public async Task<ActionResult<InquiryOverviewDto>> Overview(CancellationToken cancellationToken) =>
        Ok(await _overview.GetOverviewAsync(cancellationToken));

    [HttpGet]
    public async Task<ActionResult<PagedResult<InquiryRowDto>>> List(
        [FromQuery] string? bucket, [FromQuery] Guid? employeeId, [FromQuery] string? search,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken) =>
        Ok(await _overview.ListAsync(bucket, employeeId, search, page ?? 1, pageSize ?? 25, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<InquirySupervisionDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var dto = await _overview.GetSupervisionAsync(id, cancellationToken);
        if (dto is null) return NotFound();
        await _audit.LogAsync("INQUIRY_CONVERSATION_VIEWED", "Case", id.ToString(), $"Viewed by {_currentUser.Email}", cancellationToken);
        return Ok(dto);
    }

    [HttpPost("{id:guid}/confirm-resolved")]
    public async Task<IActionResult> ConfirmResolved(Guid id, [FromBody] ResolutionDecisionRequest request, CancellationToken cancellationToken) =>
        ToResult(await _conversation.ConfirmResolvedAsync(id, null, Note(request.Comment), cancellationToken));

    [HttpPost("{id:guid}/reject-resolution")]
    public async Task<IActionResult> RejectResolution(Guid id, [FromBody] ResolutionDecisionRequest request, CancellationToken cancellationToken) =>
        ToResult(await _conversation.RejectResolutionAsync(id, null, Note(request.Comment), cancellationToken));

    [HttpPost("{id:guid}/reassign")]
    public async Task<IActionResult> Reassign(Guid id, [FromBody] ReassignInquiryRequest request, CancellationToken cancellationToken)
    {
        var result = await _overview.ReassignAsync(id, request.EmployeeId, _currentUser.Email, cancellationToken);
        if (result.Succeeded)
        {
            await _audit.LogAsync("INQUIRY_REASSIGNED", "Case", id.ToString(), $"To employee {request.EmployeeId}", cancellationToken);
        }
        return ToResult(result);
    }

    /// <summary>Runs one Sent-folder sync and one supervision pass now (normally every 1-2 minutes).</summary>
    [HttpPost("run")]
    [Authorize(Policy = "RequireAdministrator")]
    public async Task<IActionResult> RunNow(CancellationToken cancellationToken)
    {
        var sync = await _sync.RunAsync(200, cancellationToken);
        var supervision = await _supervision.RunAsync(1000, cancellationToken);
        return Ok(new { sync, supervision });
    }

    private string? Note(string? comment) =>
        string.IsNullOrWhiteSpace(comment) ? $"(by {_currentUser.Email})" : $"{comment} (by {_currentUser.Email})";

    private IActionResult ToResult(Result<bool> result) =>
        result.Succeeded ? NoContent() : BadRequest(new { message = result.Error });
}
