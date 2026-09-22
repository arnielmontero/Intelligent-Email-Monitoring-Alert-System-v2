using Iemas.Application.ClassificationProfiles;
using Iemas.Application.ClassificationProfiles.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Iemas.Api.Controllers;

/// <summary>Requirements §28 (Classification Profiles CRUD), §29 (Classification Testing).</summary>
[ApiController]
[Route("api/v1/classification-profiles")]
[Authorize(Policy = "RequireAdministrator")]
public class ClassificationProfilesController : ControllerBase
{
    private readonly ClassificationProfileService _service;

    public ClassificationProfilesController(ClassificationProfileService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<List<ClassificationProfileDto>>> GetAll(CancellationToken cancellationToken)
    {
        return Ok(await _service.GetAllAsync(cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ClassificationProfileDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var profile = await _service.GetByIdAsync(id, cancellationToken);
        return profile is null ? NotFound() : Ok(profile);
    }

    [HttpPost]
    public async Task<ActionResult<ClassificationProfileDto>> Create([FromBody] CreateClassificationProfileRequest request, CancellationToken cancellationToken)
    {
        var result = await _service.CreateAsync(request, cancellationToken);
        if (!result.Succeeded)
        {
            return BadRequest(new { message = result.Error });
        }

        return CreatedAtAction(nameof(GetById), new { id = result.Value!.Id }, result.Value);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ClassificationProfileDto>> Update(Guid id, [FromBody] UpdateClassificationProfileRequest request, CancellationToken cancellationToken)
    {
        var result = await _service.UpdateAsync(id, request, cancellationToken);
        if (!result.Succeeded)
        {
            return result.Error == "Classification profile not found." ? NotFound(new { message = result.Error }) : BadRequest(new { message = result.Error });
        }

        return Ok(result.Value);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await _service.DeleteAsync(id, cancellationToken);
        if (!result.Succeeded)
        {
            return result.Error == "Classification profile not found." ? NotFound(new { message = result.Error }) : BadRequest(new { message = result.Error });
        }

        return NoContent();
    }

    /// <summary>§29 — must never create a real Case/notification, only run the pipeline against ad-hoc input.</summary>
    [HttpPost("test")]
    public async Task<ActionResult<TestClassificationResult>> Test([FromBody] TestClassificationRequest request, CancellationToken cancellationToken)
    {
        var result = await _service.TestClassificationAsync(request, cancellationToken);
        return result.Succeeded ? Ok(result.Value) : BadRequest(new { message = result.Error });
    }
}
