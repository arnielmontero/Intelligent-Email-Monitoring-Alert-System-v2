using Iemas.Application.Common;
using Iemas.Application.Employees;
using Iemas.Application.Employees.Dtos;
using Iemas.Domain.Identity;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iemas.Tests.Employees;

public class ReservedAddressTests
{
    private static async Task SeedAdminsAsync(TestDbContext db)
    {
        var super = new Role { Name = SystemRole.SuperAdministrator };
        var auditor = new Role { Name = SystemRole.Auditor };
        var admin = new User { Email = "admin@sawo.com", DisplayName = "System Administrator", PasswordHash = "x" };
        var reader = new User { Email = "auditor@sawo.com", DisplayName = "Auditor", PasswordHash = "x" };
        db.AddRange(super, auditor, admin, reader);
        db.UserRoles.AddRange(new UserRole { UserId = admin.Id, RoleId = super.Id }, new UserRole { UserId = reader.Id, RoleId = auditor.Id });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Employee_CannotUseTheSuperAdministratorAddress_InAnyCase()
    {
        using var db = TestDbContext.CreateNew();
        await SeedAdminsAsync(db);

        var result = await new EmployeeService(db, new NoOpAuditService())
            .CreateAsync(new CreateEmployeeRequest("Someone", "Admin@Sawo.com", null, null), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ReservedAddresses.SuperAdministratorMessage, result.Error);
        Assert.Equal(0, await db.Employees.CountAsync());
    }

    [Fact]
    public async Task Employee_MayShareAnAddressWithAnOrdinaryCmsUser()
    {
        using var db = TestDbContext.CreateNew();
        await SeedAdminsAsync(db);

        var result = await new EmployeeService(db, new NoOpAuditService())
            .CreateAsync(new CreateEmployeeRequest("Auditor Person", "auditor@sawo.com", null, null), CancellationToken.None);

        Assert.True(result.Succeeded);
    }
}
