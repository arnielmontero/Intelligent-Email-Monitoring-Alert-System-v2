using Iemas.Application.Reminders;
using Iemas.Domain.Reminders;
using Xunit;

namespace Iemas.Tests.Reminders;

public class ReminderTimingCalculatorTests
{
    private static ReminderPolicy CreatePolicy(
        bool restrictBusinessHours = true, bool excludeWeekends = true, string timeZoneId = "UTC",
        TimeSpan? start = null, TimeSpan? end = null)
    {
        return new ReminderPolicy
        {
            RestrictToBusinessHours = restrictBusinessHours,
            BusinessHoursStart = start ?? TimeSpan.FromHours(8),
            BusinessHoursEnd = end ?? TimeSpan.FromHours(17),
            ExcludeWeekends = excludeWeekends,
            TimeZoneId = timeZoneId,
        };
    }

    /// <summary>§54 "Business hours" — a candidate already inside the window is returned unchanged.</summary>
    [Fact]
    public void AdjustToAllowedWindow_AlreadyWithinWindow_ReturnsUnchanged()
    {
        var policy = CreatePolicy();
        // Wednesday 10:00 UTC.
        var candidate = new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

        var result = ReminderTimingCalculator.AdjustToAllowedWindow(policy, candidate, Array.Empty<DateOnly>());

        Assert.Equal(candidate, result);
    }

    /// <summary>§54 — a candidate before business hours defers to the start of business hours the same day.</summary>
    [Fact]
    public void AdjustToAllowedWindow_BeforeBusinessHours_DefersToStartOfDay()
    {
        var policy = CreatePolicy();
        // Wednesday 03:00 UTC — before 08:00 start.
        var candidate = new DateTimeOffset(2026, 9, 23, 3, 0, 0, TimeSpan.Zero);

        var result = ReminderTimingCalculator.AdjustToAllowedWindow(policy, candidate, Array.Empty<DateOnly>());

        Assert.Equal(new DateTimeOffset(2026, 9, 23, 8, 0, 0, TimeSpan.Zero), result);
    }

    /// <summary>§54 — a candidate after business hours defers to the start of business hours the NEXT day, never later that same day.</summary>
    [Fact]
    public void AdjustToAllowedWindow_AfterBusinessHours_DefersToNextDayStart()
    {
        var policy = CreatePolicy();
        // Wednesday 20:00 UTC — after 17:00 end.
        var candidate = new DateTimeOffset(2026, 9, 23, 20, 0, 0, TimeSpan.Zero);

        var result = ReminderTimingCalculator.AdjustToAllowedWindow(policy, candidate, Array.Empty<DateOnly>());

        Assert.Equal(new DateTimeOffset(2026, 9, 24, 8, 0, 0, TimeSpan.Zero), result);
    }

    /// <summary>§54 "Weekends" — a Saturday candidate defers to the following Monday's business-hours start.</summary>
    [Fact]
    public void AdjustToAllowedWindow_Weekend_DefersToMonday()
    {
        var policy = CreatePolicy();
        // Saturday 10:00 UTC, 2026-09-26.
        var candidate = new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);

        var result = ReminderTimingCalculator.AdjustToAllowedWindow(policy, candidate, Array.Empty<DateOnly>());

        Assert.Equal(new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero), result); // Monday
        Assert.False(ReminderTimingCalculator.IsWeekend(result));
    }

    /// <summary>§54 "Holidays" — a holiday date is skipped exactly like a weekend, deferring forward rather than sending on/before it.</summary>
    [Fact]
    public void AdjustToAllowedWindow_Holiday_DefersToNextNonHolidayDay()
    {
        var policy = CreatePolicy(excludeWeekends: false);
        var holiday = new DateOnly(2026, 9, 23);
        var candidate = new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

        var result = ReminderTimingCalculator.AdjustToAllowedWindow(policy, candidate, new[] { holiday });

        Assert.Equal(new DateTimeOffset(2026, 9, 24, 8, 0, 0, TimeSpan.Zero), result);
    }

    /// <summary>Weekend + holiday combined: a Saturday that also happens to be a holiday still lands on the next valid business day, not just the next calendar day.</summary>
    [Fact]
    public void AdjustToAllowedWindow_WeekendAndHolidayCombined_SkipsBothCorrectly()
    {
        var policy = CreatePolicy();
        // Friday holiday, 2026-09-25 — next valid day should be Monday 2026-09-28 (Sat/Sun skipped by the weekend rule).
        var holiday = new DateOnly(2026, 9, 25);
        var candidate = new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

        var result = ReminderTimingCalculator.AdjustToAllowedWindow(policy, candidate, new[] { holiday });

        Assert.Equal(new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero), result);
    }

    /// <summary>Business-hours restriction disabled — any time of day is accepted as-is (still subject to weekend/holiday rules).</summary>
    [Fact]
    public void AdjustToAllowedWindow_BusinessHoursDisabled_AcceptsAnyTimeOfDay()
    {
        var policy = CreatePolicy(restrictBusinessHours: false, excludeWeekends: false);
        var candidate = new DateTimeOffset(2026, 9, 23, 2, 0, 0, TimeSpan.Zero);

        var result = ReminderTimingCalculator.AdjustToAllowedWindow(policy, candidate, Array.Empty<DateOnly>());

        Assert.Equal(candidate, result);
    }

    /// <summary>An unrecognized time zone id falls back to UTC rather than throwing — a bad CMS-entered value must not take down scheduling.</summary>
    [Fact]
    public void ResolveTimeZone_UnknownId_FallsBackToUtc()
    {
        var tz = ReminderTimingCalculator.ResolveTimeZone("Not/A/Real/Zone");
        Assert.Equal(TimeZoneInfo.Utc.Id, tz.Id);
    }

    [Fact]
    public void ResolveTimeZone_Utc_ReturnsUtc()
    {
        var tz = ReminderTimingCalculator.ResolveTimeZone("UTC");
        Assert.Equal(TimeZoneInfo.Utc.Id, tz.Id);
    }
}
