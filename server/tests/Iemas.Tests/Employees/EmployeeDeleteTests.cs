using Iemas.Application.Employees;
using Iemas.Domain.Cases;
using Iemas.Domain.Identity;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Tests.Employees;

public class EmployeeDeleteTests
{
    private static Employee AddEmployee(TestDbContext db, bool isActive = false, string name = "Pat O'Brien")
    {
        var employee = new Employee { FullName = name, Email = $"{Guid.NewGuid():N}@sawo.com", IsActive = isActive };
        db.Employees.Add(employee);
        return employee;
    }

    [Fact]
    public async Task DeleteAsync_UnreferencedInactiveEmployee_IsDeletedUnlinksUserAndAudits()
    {
        using var db = TestDbContext.CreateNew();
        var employee = AddEmployee(db);
        var user = new User { Email = "pat@sawo.com", DisplayName = "Pat", PasswordHash = "x", EmployeeId = employee.Id };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var audit = new NoOpAuditService();

        var result = await new EmployeeService(db, audit).DeleteAsync(employee.Id, CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.False(await db.Employees.AnyAsync());
        Assert.Null((await db.Users.SingleAsync()).EmployeeId);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("EMPLOYEE_DELETED", entry.Action);
        Assert.Contains("pat@sawo.com", entry.Details);
    }

    [Fact]
    public async Task SetActiveAsync_ReactivatesAndAudits()
    {
        using var db = TestDbContext.CreateNew();
        var employee = AddEmployee(db);
        await db.SaveChangesAsync();
        var audit = new NoOpAuditService();

        var result = await new EmployeeService(db, audit).SetActiveAsync(employee.Id, true, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True((await db.Employees.SingleAsync()).IsActive);
        Assert.Equal("EMPLOYEE_REACTIVATED", Assert.Single(audit.Entries).Action);
    }

    [Fact]
    public async Task DeleteAsync_ActiveEmployee_IsRejected()
    {
        using var db = TestDbContext.CreateNew();
        var employee = AddEmployee(db, isActive: true);
        await db.SaveChangesAsync();

        var result = await new EmployeeService(db, new NoOpAuditService()).DeleteAsync(employee.Id, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Deactivate", result.Error);
    }

    [Fact]
    public async Task DeleteAsync_OwnsCases_IsKeptForHistory()
    {
        using var db = TestDbContext.CreateNew();
        var employee = AddEmployee(db);
        db.Cases.Add(new Case
        {
            CaseNumber = "CASE-000001", EmailAccountId = Guid.NewGuid(), CustomerEmailAddress = "c@example.com",
            Subject = "s", NormalizedSubject = "s", OwnerEmployeeId = employee.Id,
            FirstEmailReceivedAt = DateTimeOffset.UtcNow, LastActivityAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var result = await new EmployeeService(db, new NoOpAuditService()).DeleteAsync(employee.Id, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("1 owned Case(s)", result.Error);
        Assert.True(await db.Employees.AnyAsync());
    }

    [Fact]
    public async Task DeleteAsync_StillSupervisesSomeone_MustBeReassignedFirst()
    {
        using var db = TestDbContext.CreateNew();
        var supervisor = AddEmployee(db);
        var report = AddEmployee(db, isActive: true, name: "Report");
        report.SupervisorEmployeeId = supervisor.Id;
        await db.SaveChangesAsync();

        var result = await new EmployeeService(db, new NoOpAuditService()).DeleteAsync(supervisor.Id, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("supervisor of employee(s) (1)", result.Error);
    }
}
