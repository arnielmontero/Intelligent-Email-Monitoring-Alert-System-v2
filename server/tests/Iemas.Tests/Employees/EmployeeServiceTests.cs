using Iemas.Application.Employees;
using Iemas.Application.Employees.Dtos;
using Iemas.Domain.Identity;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iemas.Tests.Employees;

public class EmployeeServiceTests
{
    /// <summary>
    /// Requirements §12, §58 — escalation resolution walks Employee → Supervisor → Manager.
    /// A cycle would make that resolution loop forever, so creating one must be rejected.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_RejectsSupervisorAssignmentThatWouldCreateACycle()
    {
        using var db = TestDbContext.CreateNew();
        var audit = new NoOpAuditService();
        var service = new EmployeeService(db, audit);

        var a = new Employee { FullName = "A", Email = "a@sawo.com" };
        var b = new Employee { FullName = "B", Email = "b@sawo.com", SupervisorEmployeeId = null };
        db.Employees.AddRange(a, b);
        await db.SaveChangesAsync();

        // B's supervisor is A. Now try to make A's supervisor B — a 2-node cycle.
        b.SupervisorEmployeeId = a.Id;
        await db.SaveChangesAsync();

        var result = await service.UpdateAsync(a.Id, new UpdateEmployeeRequest("A", "a@sawo.com", true, null, b.Id), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("circular", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateAsync_RejectsSelfSupervision()
    {
        using var db = TestDbContext.CreateNew();
        var audit = new NoOpAuditService();
        var service = new EmployeeService(db, audit);

        var a = new Employee { FullName = "A", Email = "a@sawo.com" };
        db.Employees.Add(a);
        await db.SaveChangesAsync();

        var result = await service.UpdateAsync(a.Id, new UpdateEmployeeRequest("A", "a@sawo.com", true, null, a.Id), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("own supervisor", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateAsync_AllowsALongSupervisorChainWithoutACycle()
    {
        using var db = TestDbContext.CreateNew();
        var audit = new NoOpAuditService();
        var service = new EmployeeService(db, audit);

        // Chain: c -> b -> a (a has no supervisor)
        var a = new Employee { FullName = "A", Email = "a@sawo.com" };
        var b = new Employee { FullName = "B", Email = "b@sawo.com" };
        var c = new Employee { FullName = "C", Email = "c@sawo.com" };
        db.Employees.AddRange(a, b, c);
        await db.SaveChangesAsync();

        b.SupervisorEmployeeId = a.Id;
        await db.SaveChangesAsync();

        var result = await service.UpdateAsync(c.Id, new UpdateEmployeeRequest("C", "c@sawo.com", true, null, b.Id), CancellationToken.None);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task CreateAsync_RejectsDuplicateEmail()
    {
        using var db = TestDbContext.CreateNew();
        var audit = new NoOpAuditService();
        var service = new EmployeeService(db, audit);

        db.Employees.Add(new Employee { FullName = "Existing", Email = "dup@sawo.com" });
        await db.SaveChangesAsync();

        var result = await service.CreateAsync(new CreateEmployeeRequest("New Person", "dup@sawo.com", null, null), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("already exists", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Regression test for the Phase 11 stored-XSS finding: FullName previously accepted and
    /// persisted a script tag verbatim (confirmed live — 201 Created, stored unescaped). The write
    /// path must now reject markup rather than silently store it.
    /// </summary>
    [Fact]
    public async Task CreateAsync_RejectsHtmlMarkupInFullName()
    {
        using var db = TestDbContext.CreateNew();
        var audit = new NoOpAuditService();
        var service = new EmployeeService(db, audit);

        var result = await service.CreateAsync(
            new CreateEmployeeRequest("<script>alert(1)</script>", "xsstest@sawo.com", null, null),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(await db.Employees.ToListAsync(), e => e.Email == "xsstest@sawo.com");
    }

    [Fact]
    public async Task UpdateAsync_RejectsHtmlMarkupInFullName()
    {
        using var db = TestDbContext.CreateNew();
        var audit = new NoOpAuditService();
        var service = new EmployeeService(db, audit);

        var employee = new Employee { FullName = "Original Name", Email = "original@sawo.com" };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var result = await service.UpdateAsync(
            employee.Id,
            new UpdateEmployeeRequest("<img src=x onerror=alert(1)>", "original@sawo.com", true, null, null),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        var reloaded = await db.Employees.FirstAsync(e => e.Id == employee.Id);
        Assert.Equal("Original Name", reloaded.FullName);
    }

    [Fact]
    public async Task GetAllAsync_ResolvesManagerThroughDepartment()
    {
        using var db = TestDbContext.CreateNew();
        var audit = new NoOpAuditService();
        var service = new EmployeeService(db, audit);

        var manager = new Employee { FullName = "Manager", Email = "manager@sawo.com" };
        db.Employees.Add(manager);
        await db.SaveChangesAsync();

        var dept = new Department { Name = "Sales", ManagerEmployeeId = manager.Id };
        db.Departments.Add(dept);
        await db.SaveChangesAsync();

        var staff = new Employee { FullName = "Staff", Email = "staff@sawo.com", DepartmentId = dept.Id, SupervisorEmployeeId = manager.Id };
        db.Employees.Add(staff);
        await db.SaveChangesAsync();

        var all = await service.GetAllAsync(CancellationToken.None);
        var staffDto = all.Single(e => e.Email == "staff@sawo.com");

        Assert.Equal("Manager", staffDto.SupervisorEmployeeName);
        Assert.Equal("Manager", staffDto.ManagerEmployeeName);
    }
}
