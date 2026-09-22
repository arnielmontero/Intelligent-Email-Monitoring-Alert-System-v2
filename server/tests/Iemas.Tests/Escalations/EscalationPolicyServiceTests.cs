using Iemas.Application.Escalations;
using Iemas.Application.Escalations.Dtos;
using Iemas.Domain.Escalations;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iemas.Tests.Escalations;

public class EscalationPolicyServiceTests
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

    private static EscalationPolicyService CreateService(TestDbContext db) => new(db, new NoOpAuditService(), new EscalationService(db));

    [Fact]
    public async Task CreateAsync_ValidRequest_PersistsPolicyWithLevels()
    {
        using var db = TestDbContext.CreateNew();
        var service = CreateService(db);

        var result = await service.CreateAsync(CreateRequest(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Single(result.Value!.Levels);
        Assert.Equal(1, await db.EscalationPolicies.CountAsync());
        Assert.Equal(1, await db.EscalationLevels.CountAsync());
    }

    /// <summary>§58 "V1 can initially support a maximum of three levels."</summary>
    [Fact]
    public async Task CreateAsync_MaximumLevelGreaterThanThree_Fails()
    {
        using var db = TestDbContext.CreateNew();
        var service = CreateService(db);

        var result = await service.CreateAsync(CreateRequest(maximumLevel: 4), CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task CreateAsync_DuplicateName_Fails()
    {
        using var db = TestDbContext.CreateNew();
        var service = CreateService(db);
        await service.CreateAsync(CreateRequest(name: "Default"), CancellationToken.None);

        var result = await service.CreateAsync(CreateRequest(name: "Default", isDefault: false), CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task CreateAsync_DuplicateLevelNumbers_Fails()
    {
        using var db = TestDbContext.CreateNew();
        var service = CreateService(db);
        var levels = new List<SaveEscalationLevelRequest>
        {
            new(1, TimeSpan.Zero, EscalationRecipientType.EmployeeSupervisor, null, null),
            new(1, TimeSpan.FromDays(1), EscalationRecipientType.DepartmentManager, null, null),
        };

        var result = await service.CreateAsync(CreateRequest(levels: levels), CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task CreateAsync_SpecificEmployeeRecipientType_WithoutEmployeeId_Fails()
    {
        using var db = TestDbContext.CreateNew();
        var service = CreateService(db);
        var levels = new List<SaveEscalationLevelRequest>
        {
            new(1, TimeSpan.Zero, EscalationRecipientType.SpecificEmployee, null, null),
        };

        var result = await service.CreateAsync(CreateRequest(levels: levels), CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task CreateAsync_SpecificEmployeeRecipientType_UnknownEmployee_Fails()
    {
        using var db = TestDbContext.CreateNew();
        var service = CreateService(db);
        var levels = new List<SaveEscalationLevelRequest>
        {
            new(1, TimeSpan.Zero, EscalationRecipientType.SpecificEmployee, Guid.NewGuid(), null),
        };

        var result = await service.CreateAsync(CreateRequest(levels: levels), CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task CreateAsync_NewDefault_ClearsPreviousDefault()
    {
        using var db = TestDbContext.CreateNew();
        var service = CreateService(db);
        var first = await service.CreateAsync(CreateRequest(name: "First", isDefault: true), CancellationToken.None);

        var second = await service.CreateAsync(CreateRequest(name: "Second", isDefault: true), CancellationToken.None);

        Assert.True(second.Succeeded);
        var reloadedFirst = await db.EscalationPolicies.AsNoTracking().FirstAsync(p => p.Id == first.Value!.Id);
        Assert.False(reloadedFirst.IsDefault);
    }

    [Fact]
    public async Task UpdateAsync_ExistingPolicy_ReplacesLevels()
    {
        using var db = TestDbContext.CreateNew();
        var service = CreateService(db);
        var created = await service.CreateAsync(CreateRequest(), CancellationToken.None);

        var newLevels = new List<SaveEscalationLevelRequest>
        {
            new(1, TimeSpan.Zero, EscalationRecipientType.Employee, null, null),
            new(2, TimeSpan.FromDays(1), EscalationRecipientType.DepartmentManager, null, null),
        };
        var updated = await service.UpdateAsync(created.Value!.Id, CreateRequest(levels: newLevels), CancellationToken.None);

        Assert.True(updated.Succeeded);
        Assert.Equal(2, updated.Value!.Levels.Count);
    }

    [Fact]
    public async Task DeleteAsync_ExistingPolicy_RemovesPolicyAndLevels()
    {
        using var db = TestDbContext.CreateNew();
        var service = CreateService(db);
        var created = await service.CreateAsync(CreateRequest(), CancellationToken.None);

        var result = await service.DeleteAsync(created.Value!.Id, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0, await db.EscalationPolicies.CountAsync());
        Assert.Equal(0, await db.EscalationLevels.CountAsync());
    }

    /// <summary>§57 "Test Policy" via the service facade.</summary>
    [Fact]
    public async Task TestAsync_DelegatesToEscalationService()
    {
        using var db = TestDbContext.CreateNew();
        var service = CreateService(db);
        var created = await service.CreateAsync(CreateRequest(), CancellationToken.None);

        var result = await service.TestAsync(created.Value!.Id, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.Value!.WouldEscalate); // Case not found.
    }
}
