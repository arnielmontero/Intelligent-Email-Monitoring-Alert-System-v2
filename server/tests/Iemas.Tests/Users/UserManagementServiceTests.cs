using Iemas.Application.Users;
using Iemas.Domain.Identity;
using Iemas.Domain.Operations;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Tests.Users;

public class UserManagementServiceTests
{
    private const string GoodPassword = "Str0ngPassw0rd";

    private static async Task<TestDbContext> CreateDbAsync()
    {
        var db = TestDbContext.CreateNew();
        foreach (var name in SystemRole.All)
        {
            db.Roles.Add(new Role { Name = name });
        }
        await db.SaveChangesAsync();
        return db;
    }

    private static async Task<User> AddUserAsync(TestDbContext db, string email, params string[] roles)
    {
        var user = new User { Email = email, DisplayName = email, PasswordHash = "hashed:" + GoodPassword, IsActive = true };
        db.Users.Add(user);
        foreach (var role in roles)
        {
            var roleId = (await db.Roles.SingleAsync(r => r.Name == role)).Id;
            db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = roleId });
        }
        await db.SaveChangesAsync();
        return user;
    }

    private static UserManagementService CreateService(TestDbContext db, FakeCurrentUserService caller, NoOpAuditService? audit = null) =>
        new(db, new PlainPasswordHasher(), caller, audit ?? new NoOpAuditService());

    private static FakeCurrentUserService Admin(Guid? id = null) => new(id, "admin@sawo.com", SystemRole.Administrator);
    private static FakeCurrentUserService SuperAdmin(Guid? id = null) => new(id, "root@sawo.com", SystemRole.SuperAdministrator);

    [Fact]
    public async Task CreateAsync_ValidRequest_CreatesUserWithRolesAndAudits()
    {
        using var db = await CreateDbAsync();
        var audit = new NoOpAuditService();

        var result = await CreateService(db, Admin(), audit).CreateAsync(
            new CreateUserRequest(" Jane@Sawo.com ", "Jane Doe", GoodPassword, new() { SystemRole.SupervisorManager, SystemRole.Auditor }, null),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("jane@sawo.com", result.Value!.Email);
        Assert.Equal(new[] { SystemRole.SupervisorManager, SystemRole.Auditor }, result.Value.Roles);
        Assert.Equal("hashed:" + GoodPassword, (await db.Users.SingleAsync()).PasswordHash);
        Assert.Equal("USER_CREATED", Assert.Single(audit.Entries).Action);
        Assert.DoesNotContain(GoodPassword, audit.Entries[0].Details);
    }

    [Fact]
    public async Task CreateAsync_DuplicateEmail_IsRejected()
    {
        using var db = await CreateDbAsync();
        await AddUserAsync(db, "jane@sawo.com", SystemRole.Auditor);

        var result = await CreateService(db, Admin()).CreateAsync(
            new CreateUserRequest("JANE@sawo.com", "Jane", GoodPassword, new() { SystemRole.Auditor }, null), CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData("short1")]
    [InlineData("nodigitsatallhere")]
    [InlineData("1234567890123")]
    public async Task CreateAsync_WeakPassword_IsRejected(string password)
    {
        using var db = await CreateDbAsync();
        var result = await CreateService(db, Admin()).CreateAsync(
            new CreateUserRequest("jane@sawo.com", "Jane", password, new() { SystemRole.Auditor }, null), CancellationToken.None);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task CreateAsync_MinimumLengthComesFromSystemSettings()
    {
        using var db = await CreateDbAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "security.password_min_length", Value = "20" });
        await db.SaveChangesAsync();

        var result = await CreateService(db, Admin()).CreateAsync(
            new CreateUserRequest("jane@sawo.com", "Jane", GoodPassword, new() { SystemRole.Auditor }, null), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("20", result.Error);
    }

    [Fact]
    public async Task CreateAsync_AdministratorCannotGrantSuperAdministrator()
    {
        using var db = await CreateDbAsync();
        var result = await CreateService(db, Admin()).CreateAsync(
            new CreateUserRequest("jane@sawo.com", "Jane", GoodPassword, new() { SystemRole.SuperAdministrator }, null), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(await db.Users.AnyAsync());
    }

    [Fact]
    public async Task CreateAsync_UnknownRole_IsRejected()
    {
        using var db = await CreateDbAsync();
        var result = await CreateService(db, Admin()).CreateAsync(
            new CreateUserRequest("jane@sawo.com", "Jane", GoodPassword, new() { "Root" }, null), CancellationToken.None);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task CreateAsync_EmployeeAlreadyLinked_IsRejected()
    {
        using var db = await CreateDbAsync();
        var employee = new Employee { FullName = "Jane", Email = "jane@sawo.com" };
        db.Employees.Add(employee);
        var existing = await AddUserAsync(db, "first@sawo.com", SystemRole.Auditor);
        existing.EmployeeId = employee.Id;
        await db.SaveChangesAsync();

        var result = await CreateService(db, Admin()).CreateAsync(
            new CreateUserRequest("jane@sawo.com", "Jane", GoodPassword, new() { SystemRole.Auditor }, employee.Id), CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    /// <summary>Privilege-escalation guard: an Administrator could otherwise reset a Super Administrator's password and sign in as them.</summary>
    [Fact]
    public async Task AdministratorCannotModifySuperAdministratorAccount()
    {
        using var db = await CreateDbAsync();
        var root = await AddUserAsync(db, "root@sawo.com", SystemRole.SuperAdministrator);
        var service = CreateService(db, Admin());

        Assert.False((await service.ResetPasswordAsync(root.Id, new ResetPasswordRequest("An0therGoodPass"), CancellationToken.None)).Succeeded);
        Assert.False((await service.SetActiveAsync(root.Id, false, CancellationToken.None)).Succeeded);
        Assert.False((await service.UpdateAsync(root.Id, new UpdateUserRequest("Root", new() { SystemRole.Auditor }, null), CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task LastActiveSuperAdministrator_CannotBeDeactivatedOrDemoted()
    {
        using var db = await CreateDbAsync();
        var root = await AddUserAsync(db, "root@sawo.com", SystemRole.SuperAdministrator);
        var service = CreateService(db, SuperAdmin());

        Assert.False((await service.SetActiveAsync(root.Id, false, CancellationToken.None)).Succeeded);
        Assert.False((await service.UpdateAsync(root.Id, new UpdateUserRequest("Root", new() { SystemRole.Administrator }, null), CancellationToken.None)).Succeeded);

        await AddUserAsync(db, "root2@sawo.com", SystemRole.SuperAdministrator);
        Assert.True((await service.SetActiveAsync(root.Id, false, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task SetActiveAsync_CannotDeactivateSelf()
    {
        using var db = await CreateDbAsync();
        var me = await AddUserAsync(db, "admin@sawo.com", SystemRole.Administrator);

        var result = await CreateService(db, Admin(me.Id)).SetActiveAsync(me.Id, false, CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    /// <summary>§84 credential revocation — deactivation kills every refresh token, so the session cannot be renewed.</summary>
    [Fact]
    public async Task SetActiveAsync_Deactivate_RevokesRefreshTokens()
    {
        using var db = await CreateDbAsync();
        var user = await AddUserAsync(db, "jane@sawo.com", SystemRole.Auditor);
        db.RefreshTokens.Add(new RefreshToken { UserId = user.Id, TokenHash = "a", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) });
        db.RefreshTokens.Add(new RefreshToken { UserId = user.Id, TokenHash = "b", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) });
        await db.SaveChangesAsync();
        var audit = new NoOpAuditService();

        var result = await CreateService(db, Admin(), audit).SetActiveAsync(user.Id, false, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.Value!.IsActive);
        Assert.All(await db.RefreshTokens.ToListAsync(), t => Assert.NotNull(t.RevokedAt));
        Assert.Equal("USER_DEACTIVATED", Assert.Single(audit.Entries).Action);
    }

    [Fact]
    public async Task UpdateAsync_ChangesRoles()
    {
        using var db = await CreateDbAsync();
        var user = await AddUserAsync(db, "jane@sawo.com", SystemRole.Auditor);

        var result = await CreateService(db, Admin()).UpdateAsync(
            user.Id, new UpdateUserRequest("Jane D", new() { SystemRole.SupervisorManager }, null), CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(new[] { SystemRole.SupervisorManager }, result.Value!.Roles);
        Assert.Equal("Jane D", result.Value.DisplayName);
        Assert.Single(await db.UserRoles.Where(ur => ur.UserId == user.Id).ToListAsync());
    }

    [Fact]
    public async Task ChangeOwnPasswordAsync_WrongCurrentPassword_IsRejected()
    {
        using var db = await CreateDbAsync();
        var me = await AddUserAsync(db, "jane@sawo.com", SystemRole.Auditor);

        var result = await CreateService(db, new FakeCurrentUserService(me.Id, me.Email, SystemRole.Auditor))
            .ChangeOwnPasswordAsync(new ChangeOwnPasswordRequest("wrong", "An0therGoodPass"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("hashed:" + GoodPassword, (await db.Users.SingleAsync()).PasswordHash);
    }

    [Fact]
    public async Task ChangeOwnPasswordAsync_Valid_UpdatesHash()
    {
        using var db = await CreateDbAsync();
        var me = await AddUserAsync(db, "jane@sawo.com", SystemRole.Auditor);

        var result = await CreateService(db, new FakeCurrentUserService(me.Id, me.Email, SystemRole.Auditor))
            .ChangeOwnPasswordAsync(new ChangeOwnPasswordRequest(GoodPassword, "An0therGoodPass"), CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("hashed:An0therGoodPass", (await db.Users.SingleAsync()).PasswordHash);
    }
}
