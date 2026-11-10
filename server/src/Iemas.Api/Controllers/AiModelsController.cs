using Iemas.Application.AiModels;
using Iemas.Application.AiModels.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Iemas.Api.Controllers;

/// <summary>Requirements §82 — AI Provider/Model management CRUD, enable/disable, default, test.</summary>
[ApiController]
[Route("api/v1/ai-models")]
[Authorize(Policy = "RequireAdministrator")]
public class AiModelsController : ControllerBase
{
    private readonly AiModelService _service;
    private readonly AiProviderSettingsService _providerSettings;
    private readonly AiModelBenchmarkService _benchmark;

    public AiModelsController(AiModelService service, AiProviderSettingsService providerSettings, AiModelBenchmarkService benchmark)
    {
        _service = service;
        _providerSettings = providerSettings;
        _benchmark = benchmark;
    }

    /// <summary>The model dropdown: low-cost candidates with live price, measured accuracy and cost/effectiveness labels.</summary>
    [HttpGet("recommendations")]
    public async Task<ActionResult<IReadOnlyList<AiModelRecommendation>>> GetRecommendations(CancellationToken cancellationToken) =>
        Ok(await _benchmark.GetRecommendationsAsync(cancellationToken));

    [HttpGet("benchmark")]
    public async Task<ActionResult<IReadOnlyList<AiModelBenchmarkResult>>> GetBenchmark(CancellationToken cancellationToken) =>
        Ok(await _benchmark.GetStoredAsync(cancellationToken));

    /// <summary>Runs the benchmark email set through up to 8 models (real calls; typically well under $0.01 per model).</summary>
    [HttpPost("benchmark")]
    public async Task<ActionResult<IReadOnlyList<AiModelBenchmarkResult>>> RunBenchmark([FromBody] RunAiBenchmarkRequest request, CancellationToken cancellationToken)
    {
        if (request.Models is not { Count: > 0 }) return BadRequest(new { message = "Choose at least one model to analyze." });
        return Ok(await _benchmark.RunAsync(request.Models, cancellationToken));
    }

    /// <summary>OpenRouter connection settings. The API key is never returned, only whether one is set and its last 4 characters.</summary>
    [HttpGet("provider")]
    public async Task<ActionResult<AiProviderSettingsDto>> GetProviderSettings(CancellationToken cancellationToken) =>
        Ok(await _providerSettings.GetAsync(cancellationToken));

    [HttpPut("provider")]
    public async Task<ActionResult<AiProviderSettingsDto>> UpdateProviderSettings([FromBody] UpdateAiProviderSettingsRequest request, CancellationToken cancellationToken)
    {
        var result = await _providerSettings.UpdateAsync(request, cancellationToken);
        return result.Succeeded ? Ok(result.Value) : BadRequest(new { message = result.Error });
    }

    [HttpPost("provider/test")]
    public async Task<ActionResult<AiProviderKeyCheck>> TestProvider(CancellationToken cancellationToken) =>
        Ok(await _providerSettings.TestAsync(cancellationToken));

    [HttpGet("provider/catalog")]
    public async Task<ActionResult<IReadOnlyList<AiCatalogModel>>> GetCatalog(CancellationToken cancellationToken) =>
        Ok(await _providerSettings.GetCatalogAsync(cancellationToken));

    [HttpGet]
    public async Task<ActionResult<List<AiModelDto>>> GetAll(CancellationToken cancellationToken)
    {
        return Ok(await _service.GetAllAsync(cancellationToken));
    }

    [HttpPost]
    public async Task<ActionResult<AiModelDto>> Create([FromBody] CreateAiModelRequest request, CancellationToken cancellationToken)
    {
        var result = await _service.CreateAsync(request, cancellationToken);
        return result.Succeeded ? Ok(result.Value) : BadRequest(new { message = result.Error });
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<AiModelDto>> Update(Guid id, [FromBody] UpdateAiModelRequest request, CancellationToken cancellationToken)
    {
        var result = await _service.UpdateAsync(id, request, cancellationToken);
        if (!result.Succeeded)
        {
            return result.Error == "AI model configuration not found." ? NotFound(new { message = result.Error }) : BadRequest(new { message = result.Error });
        }

        return Ok(result.Value);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await _service.DeleteAsync(id, cancellationToken);
        return result.Succeeded ? NoContent() : NotFound(new { message = result.Error });
    }

    [HttpPost("{id:guid}/test")]
    public async Task<ActionResult<TestAiModelResult>> Test(Guid id, CancellationToken cancellationToken)
    {
        var result = await _service.TestAsync(id, cancellationToken);
        return result.Succeeded ? Ok(result.Value) : NotFound(new { message = result.Error });
    }
}

public record RunAiBenchmarkRequest(List<string>? Models);
