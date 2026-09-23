// Tests the controller's own action logic (service invocation, result mapping); [Authorize] policy
// enforcement itself requires the full HTTP pipeline and is not exercised here — same acknowledged
// boundary as every prior phase's RBAC verification gap.
using Iemas.Api.Controllers;
using Iemas.Application.Escalations;
using Iemas.Application.Escalations.Dtos;
using Iemas.Domain.Escalations;
using Iemas.Domain.Identity;
using Iemas.Tests.TestSupport;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iemas.Tests.Escalations;

public class EscalationGroupsControllerTests
{
    private static EscalationGroupsController CreateController(TestDbContext db) =>
        new(new EscalationGroupService(db, new NoOpAuditService()));

    [Fact]
    public async Task GetAll_ReturnsOk_WithExpectedCount()
    {
        using var db = TestDbContext.CreateNew();
        var controller = CreateController(db);
        await controller.Create(new SaveEscalationGroupRequest("Team A", Array.Empty<Guid>()), CancellationToken.None);
        await controller.Create(new SaveEscalationGroupRequest("Team B", Array.Empty<Guid>()), CancellationToken.None);

        var result = await controller.GetAll(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsAssignableFrom<List<EscalationGroupDto>>(ok.Value);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task Create_Valid_ReturnsOk()
    {
        using var db = TestDbContext.CreateNew();
        var controller = CreateController(db);

        var result = await controller.Create(new SaveEscalationGroupRequest("Team", Array.Empty<Guid>()), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<EscalationGroupDto>(ok.Value);
    }

    [Fact]
    public async Task Create_UnknownEmployee_ReturnsBadRequest()
    {
        using var db = TestDbContext.CreateNew();
        var controller = CreateController(db);

        var result = await controller.Create(new SaveEscalationGroupRequest("Team", new[] { Guid.NewGuid() }), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.NotNull(bad.Value);
    }

    [Fact]
    public async Task Update_Valid_ReturnsOk()
    {
        using var db = TestDbContext.CreateNew();
        var controller = CreateController(db);
        var created = await controller.Create(new SaveEscalationGroupRequest("Team", Array.Empty<Guid>()), CancellationToken.None);
        var createdDto = (EscalationGroupDto)((OkObjectResult)created.Result!).Value!;

        var result = await controller.Update(createdDto.Id, new SaveEscalationGroupRequest("Renamed", Array.Empty<Guid>()), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<EscalationGroupDto>(ok.Value);
        Assert.Equal("Renamed", dto.Name);
    }

    [Fact]
    public async Task Update_NotFound_ReturnsNotFound()
    {
        using var db = TestDbContext.CreateNew();
        var controller = CreateController(db);

        var result = await controller.Update(Guid.NewGuid(), new SaveEscalationGroupRequest("Team", Array.Empty<Guid>()), CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.NotNull(notFound.Value);
    }

    [Fact]
    public async Task Update_ValidationFailure_ReturnsBadRequest()
    {
        using var db = TestDbContext.CreateNew();
        var controller = CreateController(db);
        var created = await controller.Create(new SaveEscalationGroupRequest("Team", Array.Empty<Guid>()), CancellationToken.None);
        var createdDto = (EscalationGroupDto)((OkObjectResult)created.Result!).Value!;

        var result = await controller.Update(createdDto.Id, new SaveEscalationGroupRequest("Team2", new[] { Guid.NewGuid() }), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Delete_Existing_ReturnsNoContent()
    {
        using var db = TestDbContext.CreateNew();
        var controller = CreateController(db);
        var created = await controller.Create(new SaveEscalationGroupRequest("Team", Array.Empty<Guid>()), CancellationToken.None);
        var createdDto = (EscalationGroupDto)((OkObjectResult)created.Result!).Value!;

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

    /// <summary>Delete-protection while a group is in use by an Escalation Level — surfaced as BadRequest with the service's message.</summary>
    [Fact]
    public async Task Delete_GroupInUseByEscalationLevel_ReturnsBadRequest()
    {
        using var db = TestDbContext.CreateNew();
        var controller = CreateController(db);
        var created = await controller.Create(new SaveEscalationGroupRequest("Team", Array.Empty<Guid>()), CancellationToken.None);
        var createdDto = (EscalationGroupDto)((OkObjectResult)created.Result!).Value!;

        db.EscalationPolicies.Add(new EscalationPolicy { Name = "P", Channel = "Email" });
        await db.SaveChangesAsync();
        var policy = await db.EscalationPolicies.FirstAsync();
        db.EscalationLevels.Add(new EscalationLevel
        {
            EscalationPolicyId = policy.Id, Level = 1, RecipientType = EscalationRecipientType.SpecificGroup, SpecificGroupId = createdDto.Id,
        });
        await db.SaveChangesAsync();

        var result = await controller.Delete(createdDto.Id, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(bad.Value);
    }
}
