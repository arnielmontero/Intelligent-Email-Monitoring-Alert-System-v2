using Iemas.Application.Agents;
using Iemas.Application.Agents.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Iemas.Api.Controllers;

/// <summary>
/// Requirements §46 (Employee Actions), §47 (Employee Comments), §73 (Agent-to-Server Actions),
/// §75 (Synchronization) as REST fallbacks alongside the SignalR hub (§76 — SignalR is a
/// transport, not the only path; a REST fallback means an action isn't lost if the socket is
/// briefly down, matching §77 "the Agent should be able to rebuild its current UI from a fresh
/// server synchronization" including via a plain HTTP call).
///
/// Every action is scoped to the authenticated Agent's own Agent ID / Employee ID from its JWT
/// claims — never trusted from the request body (§73 "Server must validate that the Agent is
/// authorized for the Employee and Case").
/// </summary>
[ApiController]
[Route("api/v1/agent")]
[Authorize(Policy = "RequireAgent")]
public class AgentOperationsController : ControllerBase
{
    private readonly AgentSyncService _syncService;
    private readonly AgentCaseActionService _actionService;

    private readonly Iemas.Application.Inquiries.ConversationSyncService _conversationSync;

    public AgentOperationsController(AgentSyncService syncService, AgentCaseActionService actionService,
        Iemas.Application.Inquiries.ConversationSyncService conversationSync)
    {
        _syncService = syncService;
        _actionService = actionService;
        _conversationSync = conversationSync;
    }

    /// <summary>
    /// The employee's replies read from Outlook's Sent Items by the Agent (for mailboxes Outlook uses as POP,
    /// whose sent mail never reaches the server). Attached only to this employee's own open inquiries.
    /// </summary>
    [HttpPost("sent-messages")]
    public async Task<ActionResult<AgentSentMessagesResult>> ReportSentMessages([FromBody] AgentSentMessagesRequest request, CancellationToken cancellationToken)
    {
        var messages = (request.Messages ?? new()).Take(100)
            .Where(m => !string.IsNullOrWhiteSpace(m.EntryId) && !string.IsNullOrWhiteSpace(m.From) && m.To is { Count: > 0 })
            .Select(m => m.ToProviderMessage())
            .ToList();
        var (agent, internalCount) = await _conversationSync.IngestFromAgentAsync(EmployeeId, messages, cancellationToken);
        return Ok(new AgentSentMessagesResult(messages.Count, agent, internalCount));
    }

    private Guid AgentId => Guid.Parse(User.FindFirst("agent_id")!.Value);
    private Guid EmployeeId => Guid.Parse(User.FindFirst("employee_id")!.Value);

    [HttpGet("sync")]
    public async Task<ActionResult<AgentSyncResponse>> Sync(CancellationToken cancellationToken)
    {
        var result = await _syncService.SyncAsync(AgentId, EmployeeId, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat([FromBody] HeartbeatRequest request, CancellationToken cancellationToken)
    {
        var ok = await _syncService.RecordHeartbeatAsync(AgentId, request, cancellationToken);
        return ok ? NoContent() : NotFound();
    }

    [HttpPost("errors")]
    public async Task<IActionResult> ReportError([FromBody] string detail, CancellationToken cancellationToken)
    {
        await _syncService.RecordErrorAsync(AgentId, detail, cancellationToken);
        return NoContent();
    }

    [HttpPost("case-actions")]
    public async Task<ActionResult<CaseActionResultDto>> SubmitCaseAction([FromBody] SubmitCaseActionRequest request, CancellationToken cancellationToken)
    {
        var result = await _actionService.SubmitActionAsync(AgentId, EmployeeId, request, cancellationToken);
        return result.Succeeded ? Ok(result.Value) : BadRequest(new { message = result.Error });
    }

    [HttpPost("case-comments")]
    public async Task<IActionResult> SubmitCaseComment([FromBody] SubmitCaseCommentRequest request, CancellationToken cancellationToken)
    {
        var result = await _actionService.SubmitCommentAsync(AgentId, EmployeeId, request, cancellationToken);
        return result.Succeeded ? NoContent() : BadRequest(new { message = result.Error });
    }

    /// <summary>§48/§46 MARK_COMPLETED — the Agent-reachable counterpart to CasesController's admin-only /complete; requires a reason, ownership-checked against this Agent's own Employee.</summary>
    [HttpPost("cases/{id:guid}/complete")]
    public async Task<IActionResult> CompleteCase(Guid id, [FromBody] CompleteCaseActionRequest request, CancellationToken cancellationToken)
    {
        var result = await _actionService.CompleteCaseAsync(AgentId, EmployeeId, id, request.Reason, request.Comment, cancellationToken);
        if (!result.Succeeded)
        {
            return result.Error == "Case not found." ? NotFound(new { message = result.Error }) : BadRequest(new { message = result.Error });
        }

        return NoContent();
    }
}
