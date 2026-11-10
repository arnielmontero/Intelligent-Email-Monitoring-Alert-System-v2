using Iemas.Application.Operations;

namespace Iemas.Tests.Operations;

/// <summary>The settings page shows each number with what it counts. A number with no unit leaves the admin guessing (hours? days? percent?).</summary>
public class SettingsCatalogTests
{
    [Fact]
    public void EveryNumberSetting_SaysWhatItCounts()
    {
        var missing = SystemSettingsService.Catalog
            .Where(d => d.Type == SettingValueType.Integer && string.IsNullOrWhiteSpace(d.Unit))
            .Select(d => d.Key)
            .ToList();

        Assert.True(missing.Count == 0, $"These number settings have no unit: {string.Join(", ", missing)}");
    }

    [Theory]
    [InlineData(SystemSettingKeys.FirstResponseBusinessHours, "business hours")]
    [InlineData(SystemSettingKeys.ReminderIntervalBusinessHours, "business hours")]
    [InlineData(SystemSettingKeys.IdleBusinessDays, "business days")]
    [InlineData(SystemSettingKeys.ResolutionTargetBusinessDays, "business days")]
    [InlineData(SystemSettingKeys.CustomerWaitCalendarDays, "calendar days")]
    public void SupervisionTimers_StateWhetherTheyCountBusinessTimeOrCalendarTime(string key, string unit)
    {
        Assert.Equal(unit, SystemSettingsService.Catalog.Single(d => d.Key == key).Unit);
    }
}
