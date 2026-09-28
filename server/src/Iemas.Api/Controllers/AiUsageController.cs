using Iemas.Application.AiModels;
using Iemas.Application.AiUsage;
using Iemas.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Iemas.Api.Controllers;

/// <summary>AI cost monitoring: totals, per-model breakdown, the OpenRouter account balance and a paged call list.</summary>
[ApiController]
[Route("api/v1/ai-usage")]
[Authorize(Policy = "RequireAdministrator")]
public class AiUsageController : ControllerBase
{
    private readonly AiUsageQueryService _queryService;
    private readonly AiProviderSettingsService _providerSettings;

    public AiUsageController(AiUsageQueryService queryService, AiProviderSettingsService providerSettings)
    {
        _queryService = queryService;
        _providerSettings = providerSettings;
    }

    [HttpGet("summary")]
    public async Task<ActionResult<AiUsageSummaryDto>> Summary(CancellationToken cancellationToken) =>
        Ok(await _queryService.GetSummaryAsync(cancellationToken));

    [HttpGet("calls")]
    public async Task<ActionResult<PagedResult<AiUsageCallDto>>> Calls(
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromQuery] string? model,
        [FromQuery] string? purpose,
        [FromQuery] bool? succeeded,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken cancellationToken) =>
        Ok(await _queryService.GetCallsAsync(page ?? 1, pageSize ?? AiUsageQueryService.DefaultPageSize, model, purpose, succeeded, from, to, cancellationToken));

    [HttpGet("models")]
    public async Task<ActionResult<List<string>>> Models(CancellationToken cancellationToken) =>
        Ok(await _queryService.GetModelsUsedAsync(cancellationToken));

    /// <summary>Live OpenRouter account usage and limit for the configured key (no credit is spent).</summary>
    [HttpGet("account")]
    public async Task<ActionResult<AiProviderKeyCheck>> Account(CancellationToken cancellationToken) =>
        Ok(await _providerSettings.TestAsync(cancellationToken));
}
