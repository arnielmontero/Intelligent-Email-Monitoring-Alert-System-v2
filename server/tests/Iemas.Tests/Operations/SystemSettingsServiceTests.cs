using Iemas.Application.Operations;
using Iemas.Domain.Identity;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Tests.Operations;

public class SystemSettingsServiceTests
{
    private static SystemSettingsService CreateService(TestDbContext db, NoOpAuditService? audit = null) =>
        new(db, new FakeCurrentUserService(roles: SystemRole.Administrator), audit ?? new NoOpAuditService());

    [Fact]
    public async Task GetAllAsync_NoOverrides_ReturnsCatalogDefaults()
    {
        using var db = TestDbContext.CreateNew();
        var settings = await CreateService(db).GetAllAsync(CancellationToken.None);

        Assert.Equal(SystemSettingsService.Catalog.Count, settings.Count);
        Assert.All(settings, s => Assert.False(s.IsOverridden));
        Assert.Equal("10", settings.Single(s => s.Key == SystemSettingKeys.PasswordMinLength).Value);
    }

    [Fact]
    public async Task UpdateAsync_ValidValues_StoresAndAuditsEachChange()
    {
        using var db = TestDbContext.CreateNew();
        var audit = new NoOpAuditService();

        var result = await CreateService(db, audit).UpdateAsync(new UpdateSystemSettingsRequest(new()
        {
            [SystemSettingKeys.OrganizationName] = "  SAWO  ",
            [SystemSettingKeys.DefaultTimeZone] = "Asia/Manila",
            ["retention.audit_logs"] = "365",
        }), CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("SAWO", await SystemSettingsService.GetValueAsync(db, SystemSettingKeys.OrganizationName, CancellationToken.None));
        Assert.Equal(3, audit.Entries.Count(e => e.Action == "SYSTEM_SETTING_UPDATED"));
    }

    /// <summary>All-or-nothing: one invalid value rejects the whole update.</summary>
    [Theory]
    [InlineData("security.password_min_length", "4")]
    [InlineData("security.password_min_length", "ten")]
    [InlineData("general.default_time_zone", "Mars/Olympus")]
    [InlineData("general.organization_name", "<b>x</b>")]
    [InlineData("general.unknown_key", "1")]
    public async Task UpdateAsync_InvalidValue_RejectsEverything(string key, string value)
    {
        using var db = TestDbContext.CreateNew();
        var result = await CreateService(db).UpdateAsync(new UpdateSystemSettingsRequest(new()
        {
            ["retention.cases"] = "30",
            [key] = value,
        }), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(await db.SystemSettings.AnyAsync());
    }

    [Fact]
    public async Task ResetAsync_RemovesOverride()
    {
        using var db = TestDbContext.CreateNew();
        var service = CreateService(db);
        await service.UpdateAsync(new UpdateSystemSettingsRequest(new() { [SystemSettingKeys.PasswordMinLength] = "16" }), CancellationToken.None);

        var result = await service.ResetAsync(SystemSettingKeys.PasswordMinLength, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(10, await SystemSettingsService.GetIntAsync(db, SystemSettingKeys.PasswordMinLength, CancellationToken.None));
    }
}
