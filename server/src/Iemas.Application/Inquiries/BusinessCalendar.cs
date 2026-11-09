using Iemas.Application.Common.Interfaces;
using Iemas.Application.Operations;
using Iemas.Application.Reminders;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Inquiries;

/// <summary>
/// Q1 — agent response and resolution deadlines count business time only: business hours, no
/// weekends, no configured holidays. Customer waits use calendar days and do not use this class.
///
/// The calendar comes from the default Reminder Policy (time zone, hours, weekends, holidays), so
/// holidays are managed in one place in the CMS. Without a default policy: Mon-Fri 08:00-17:00 in
/// the system's default time zone.
/// </summary>
public sealed class BusinessCalendar
{
    private const int MaxDays = 3660;

    private readonly TimeZoneInfo _zone;
    private readonly TimeSpan _start;
    private readonly TimeSpan _end;
    private readonly bool _excludeWeekends;
    private readonly HashSet<DateOnly> _holidays;
    private readonly double _scale = 1.0;

    public BusinessCalendar(TimeZoneInfo zone, TimeSpan start, TimeSpan end, bool excludeWeekends, IEnumerable<DateOnly> holidays)
    {
        _zone = zone;
        _start = start;
        _end = end > start ? end : start + TimeSpan.FromHours(1);
        _excludeWeekends = excludeWeekends;
        _holidays = holidays.ToHashSet();
    }

    /// <summary>Length of one business day, e.g. 9 hours for 08:00-17:00. "1 business day" means this much business time.</summary>
    public TimeSpan BusinessDayLength => _end - _start;

    /// <summary>
    /// Test mode only: round the clock, every day, and 60 times faster (1 hour of configured time = 1 minute,
    /// 1 business day = 24 minutes). Lets a person see reminders and escalation within minutes.
    /// </summary>
    public static BusinessCalendar FastTestClock() =>
        new(TimeZoneInfo.Utc, TimeSpan.Zero, TimeSpan.FromHours(24), false, Array.Empty<DateOnly>(), 1.0 / 60);

    private BusinessCalendar(TimeZoneInfo zone, TimeSpan start, TimeSpan end, bool excludeWeekends, IEnumerable<DateOnly> holidays, double scale)
        : this(zone, start, end, excludeWeekends, holidays) => _scale = scale;

    public static readonly BusinessCalendar Default = new(TimeZoneInfo.Utc, TimeSpan.FromHours(8), TimeSpan.FromHours(17), true, Array.Empty<DateOnly>());

    public static async Task<BusinessCalendar> LoadAsync(IAppDbContext db, CancellationToken cancellationToken)
    {
        var policy = await db.ReminderPolicies.AsNoTracking().Include(p => p.Holidays)
            .Where(p => p.Enabled && p.IsDefault)
            .FirstOrDefaultAsync(cancellationToken);

        if (policy is null)
        {
            var zone = await SystemSettingsService.GetDefaultTimeZoneAsync(db, cancellationToken);
            return new BusinessCalendar(zone, TimeSpan.FromHours(8), TimeSpan.FromHours(17), true, Array.Empty<DateOnly>());
        }

        var start = policy.RestrictToBusinessHours ? policy.BusinessHoursStart : TimeSpan.Zero;
        var end = policy.RestrictToBusinessHours ? policy.BusinessHoursEnd : TimeSpan.FromHours(24);
        return new BusinessCalendar(ReminderTimingCalculator.ResolveTimeZone(policy.TimeZoneId), start, end, policy.ExcludeWeekends,
            policy.Holidays.Select(h => h.Date));
    }

    public bool IsBusinessDay(DateOnly localDate) =>
        !(_excludeWeekends && localDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) && !_holidays.Contains(localDate);

    /// <summary>The instant <paramref name="amount"/> of business time after <paramref name="fromUtc"/>. Time outside business hours does not count.</summary>
    public DateTimeOffset AddBusinessTime(DateTimeOffset fromUtc, TimeSpan amount)
    {
        if (amount <= TimeSpan.Zero) return fromUtc;
        amount = TimeSpan.FromTicks((long)(amount.Ticks * _scale));

        var local = TimeZoneInfo.ConvertTime(fromUtc, _zone);
        var date = DateOnly.FromDateTime(local.DateTime);
        var timeOfDay = local.TimeOfDay;
        var remaining = amount;

        for (var i = 0; i < MaxDays; i++)
        {
            if (IsBusinessDay(date))
            {
                var windowStart = timeOfDay < _start ? _start : timeOfDay;
                if (windowStart < _end)
                {
                    var available = _end - windowStart;
                    if (remaining <= available)
                    {
                        return ToUtc(date, windowStart + remaining);
                    }
                    remaining -= available;
                }
            }

            date = date.AddDays(1);
            timeOfDay = TimeSpan.Zero;
        }

        return fromUtc + amount;
    }

    public DateTimeOffset AddBusinessDays(DateTimeOffset fromUtc, int days) =>
        AddBusinessTime(fromUtc, TimeSpan.FromTicks(BusinessDayLength.Ticks * days));

    public DateTimeOffset AddBusinessHours(DateTimeOffset fromUtc, double hours) =>
        AddBusinessTime(fromUtc, TimeSpan.FromHours(hours));

    /// <summary>Plain calendar days (customer waits). Only shortened in test mode.</summary>
    public DateTimeOffset AddCalendarDays(DateTimeOffset fromUtc, int days) =>
        fromUtc + TimeSpan.FromTicks((long)(TimeSpan.FromDays(days).Ticks * _scale));

    private DateTimeOffset ToUtc(DateOnly date, TimeSpan timeOfDay)
    {
        var unspecified = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue) + timeOfDay, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, _zone.GetUtcOffset(unspecified)).ToUniversalTime();
    }
}

/// <summary>All Q1/Q2 supervision settings, read once per run.</summary>
public sealed record SupervisionSettings(
    bool Enabled,
    int FirstResponseBusinessHours,
    int ReminderIntervalBusinessHours,
    int RemindersBeforeEscalation,
    int IdleBusinessDays,
    int CustomerWaitCalendarDays,
    int InternalWaitBusinessDays,
    int ResolutionTargetBusinessDays,
    int EscalationRepeatBusinessDays,
    bool AiResolutionEnabled,
    int AiResolutionMinConfidencePercent,
    int AgentReplyRetentionDays,
    bool TestClockMinutes = false)
{
    /// <summary>The product owner's Q1/Q2 defaults - also what an empty settings table yields.</summary>
    public static readonly SupervisionSettings Defaults = new(true, 4, 2, 2, 1, 3, 1, 5, 1, true, 75, 365);

    public static async Task<SupervisionSettings> LoadAsync(IAppDbContext db, CancellationToken cancellationToken)
    {
        Task<int> Int(string key) => SystemSettingsService.GetIntAsync(db, key, cancellationToken);
        return new SupervisionSettings(
            await SystemSettingsService.GetBoolAsync(db, SystemSettingKeys.SupervisionEnabled, cancellationToken),
            await Int(SystemSettingKeys.FirstResponseBusinessHours),
            await Int(SystemSettingKeys.ReminderIntervalBusinessHours),
            await Int(SystemSettingKeys.RemindersBeforeEscalation),
            await Int(SystemSettingKeys.IdleBusinessDays),
            await Int(SystemSettingKeys.CustomerWaitCalendarDays),
            await Int(SystemSettingKeys.InternalWaitBusinessDays),
            await Int(SystemSettingKeys.ResolutionTargetBusinessDays),
            await Int(SystemSettingKeys.EscalationRepeatBusinessDays),
            await SystemSettingsService.GetBoolAsync(db, SystemSettingKeys.AiResolutionEnabled, cancellationToken),
            await Int(SystemSettingKeys.AiResolutionMinConfidence),
            await Int(SystemSettingKeys.AgentReplyRetentionDays),
            await SystemSettingsService.GetBoolAsync(db, SystemSettingKeys.TestClockMinutes, cancellationToken));
    }
}
