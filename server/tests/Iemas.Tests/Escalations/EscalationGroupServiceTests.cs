using Iemas.Application.Escalations;
using Iemas.Application.Escalations.Dtos;
using Iemas.Domain.Escalations;
using Iemas.Domain.Identity;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iemas.Tests.Escalations;

public class EscalationGroupServiceTests
{
    private static EscalationGroupService CreateService(TestDbContext db) => new(db, new NoOpAuditService());

    [Fact]
    public async Task CreateAsync_ValidRequest_PersistsGroupWithMembers()
    {
        using var db = TestDbContext.CreateNew();
        var emp1 = new Employee { FullName = "A", Email = "a@sawo.com" };
        var emp2 = new Employee { FullName = "B", Email = "b@sawo.com" };
        db.Employees.AddRange(emp1, emp2);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var result = await service.CreateAsync(new SaveEscalationGroupRequest("Team", new[] { emp1.Id, emp2.Id }), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Value!.Members.Count);
    }

    [Fact]
    public async Task CreateAsync_UnknownEmployee_Fails()
    {
        using var db = TestDbContext.CreateNew();
        var service = CreateService(db);

        var result = await service.CreateAsync(new SaveEscalationGroupRequest("Team", new[] { Guid.NewGuid() }), CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task DeleteAsync_GroupInUseByEscalationLevel_Fails()
    {
        using var db = TestDbContext.CreateNew();
        var service = CreateService(db);
        var created = await service.CreateAsync(new SaveEscalationGroupRequest("Team", Array.Empty<Guid>()), CancellationToken.None);

        db.EscalationPolicies.Add(new EscalationPolicy { Name = "P", Channel = "Email" });
        await db.SaveChangesAsync();
        var policy = await db.EscalationPolicies.FirstAsync();
        db.EscalationLevels.Add(new EscalationLevel
        {
            EscalationPolicyId = policy.Id, Level = 1, RecipientType = EscalationRecipientType.SpecificGroup, SpecificGroupId = created.Value!.Id,
        });
        await db.SaveChangesAsync();

        var result = await service.DeleteAsync(created.Value.Id, CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task DeleteAsync_UnusedGroup_Succeeds()
    {
        using var db = TestDbContext.CreateNew();
        var service = CreateService(db);
        var created = await service.CreateAsync(new SaveEscalationGroupRequest("Team", Array.Empty<Guid>()), CancellationToken.None);

        var result = await service.DeleteAsync(created.Value!.Id, CancellationToken.None);

        Assert.True(result.Succeeded);
    }
}
