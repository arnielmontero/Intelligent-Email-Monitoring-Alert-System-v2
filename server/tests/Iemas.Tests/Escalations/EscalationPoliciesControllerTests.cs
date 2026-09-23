// Tests the controller's own action logic (service invocation, result mapping); [Authorize] policy
// enforcement itself requires the full HTTP pipeline and is not exercised here — same acknowledged
// boundary as every prior phase's RBAC verification gap.
using Iemas.Api.Controllers;
using Iemas.Application.Escalations;
using Iemas.Application.Escalations.Dtos;
using Iemas.Domain.Escalations;
using Iemas.Tests.TestSupport;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Iemas.Tests.Escalations;

public class EscalationPoliciesControllerTests
{
    private static SaveEscalationPolicyRequest CreateRequest(
        string name = "Default", bool isDefault = true, int maximumLevel = 3,
        IReadOnlyList<SaveEscalationLevelRequest>? levels = null)
    {
        return new SaveEscalationPolicyRequest(
            name, "Description", true, isDefault, null, null, null,
            3, TimeSpan.FromDays(2), TimeSpan.FromDays(1), maximumLevel, "Email",
            levels ?? new List<SaveEscalationLevelRequest>
            {
                new(1, TimeSpan.Zero, EscalationRecipientType.EmployeeSupervisor, null, null),
            });
    }

    private static EscalationPoliciesController CreateController(TestDbContext db) =>
        new(new EscalationPolicyService(db, new NoOpAuditService(), new EscalationService(db)));

    [Fact]
    public async Task GetAll_ReturnsOk_WithExpectedCount()
    {
        using var db = TestDbContext.CreateNew();
        var controller = CreateController(db);
        await controller.Create(CreateRequest(name: "A"), CancellationToken.None);
        await controller.Create(CreateRequest(name: "B", isDefault: false), CancellationToken.None);

        var result = await controller.GetAll(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsAssignableFrom<List<EscalationPolicyDto>>(ok.Value);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task GetById_Found_ReturnsOk()
    {
        using var db = TestDbContext.CreateNew();
        var controller = CreateController(db);
        var created = await controller.Create(CreateRequest(), CancellationToken.None);
        var createdDto = Assert.IsType<OkObjectResult>(created.Result).Value as EscalationPolicyDto;

        var result = await controller.GetById(createdDto!.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<EscalationPolicyDto>(ok.Value);
        Assert.Equal(createdDto.Id, dto.Id);
    }

    [Fact]
    public async Task GetById_NotFound_ReturnsNotFound()
    {
        using var db = TestDbContext.CreateNew();
        var controller = CreateController(db);

        var result = await controller.GetById(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Create_Valid_ReturnsOk()
    {
        using var db = TestDbContext.CreateNew();
        var controller = CreateController(db);

        var result = await controller.Create(CreateRequest(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<EscalationPolicyDto>(ok.Value);
    }

    /// <summary>§58 validation failure surfaced through the controller as BadRequest, not a raw exception.</summary>
    [Fact]
    public async Task Create_MaximumLevelInvalid_ReturnsBadRequestWithMessage()
    {
        using var db = TestDbContext.CreateNew();
        var controller = CreateController(db);

        var result = await controller.Create(CreateRequest(maximumLevel: 4), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.NotNull(bad.Value);
    }

    [Fact]
    public async Task Update_Valid_ReturnsOk()
    {
        using var db = TestDbContext.CreateNew();
        var controller = CreateController(db);
        var created = await controller.Create(CreateRequest(), CancellationToken.None);
        var createdDto = (EscalationPolicyDto)((OkObjectResult)created.Result!).Value!;

        var result = await controller.Update(createdDto.Id, CreateRequest(name: "Renamed"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<EscalationPolicyDto>(ok.Value);
        Assert.Equal("Renamed", dto.Name);
    }

    [Fact]
    public async Task Update_NotFound_ReturnsNotFound()
    {
        using var db = TestDbContext.CreateNew();
        var controller = CreateController(db);

        var result = await controller.Update(Guid.NewGuid(), CreateRequest(), CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.NotNull(notFound.Value);
    }

    [Fact]
    public async Task Update_ValidationFailure_ReturnsBadRequest()
    {
        using var db = TestDbContext.CreateNew();
        var controller = CreateController(db);
        var created = await controller.Create(CreateRequest(), CancellationToken.None);
        var createdDto = (EscalationPolicyDto)((OkObjectResult)created.Result!).Value!;

        var result = await controller.Update(createdDto.Id, CreateRequest(maximumLevel: 5), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Delete_Existing_ReturnsNoContent()
    {
        using var db = TestDbContext.CreateNew();
        var controller = CreateController(db);
        var created = await controller.Create(CreateRequest(), CancellationToken.None);
        var createdDto = (EscalationPolicyDto)((OkObjectResult)created.Result!).Value!;

        var result = await controller.Delete(createdDto.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task Delete_NotFound_ReturnsNotFound()
    {
        using var db = TestDbContext.CreateNew();
        var controller = CreateController(db);

        var result = await controller.Delete(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    /// <summary>§57 Test Policy endpoint — dry-run, always returns Ok since the service wraps the result in Result.Success.</summary>
    [Fact]
    public async Task Test_ReturnsOk()
    {
        using var db = TestDbContext.CreateNew();
        var controller = CreateController(db);
        var created = await controller.Create(CreateRequest(), CancellationToken.None);
        var createdDto = (EscalationPolicyDto)((OkObjectResult)created.Result!).Value!;

        var result = await controller.Test(createdDto.Id, Guid.NewGuid(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<TestEscalationPolicyResult>(ok.Value);
    }
}
