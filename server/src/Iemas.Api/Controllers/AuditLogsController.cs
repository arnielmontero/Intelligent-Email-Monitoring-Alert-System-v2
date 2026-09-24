using Iemas.Application.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Iemas.Api.Controllers;

/// <summary>
/// Requirements §67, §84, §85 (Auditor/read-only role — "Authorized history and audit access"),
/// §86 (HISTORY & AUDIT → Audit Log). Read-only: this is an append-only log, and this controller
/// has no write action at all — every entry is written by the feature that generated it via
/// IAuditService, never through this API surface.
/// </summary>
[ApiController]
[Route("api/v1/audit-logs")]
[Authorize(Policy = "RequireAuditorOrAbove")]
public class AuditLogsController : ControllerBase
{
    private readonly AuditQueryService _queryService;

    public AuditLogsController(AuditQueryService queryService)
    {
        _queryService = queryService;
    }

    [HttpGet]
    public async Task<ActionResult<List<AuditLogDto>>> Search(
        [FromQuery] string? action,
        [FromQuery] string? entityType,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int? take,
        CancellationToken cancellationToken)
    {
        return Ok(await _queryService.SearchAsync(action, entityType, from, to, take ?? 100, cancellationToken));
    }
}
