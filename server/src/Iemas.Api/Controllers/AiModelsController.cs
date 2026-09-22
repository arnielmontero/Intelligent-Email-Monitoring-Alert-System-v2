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

    public AiModelsController(AiModelService service)
    {
        _service = service;
    }

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
