using Iemas.Domain.Reminders;

namespace Iemas.Application.Reminders;

/// <summary>
/// Requirements §54 (Business hours / Weekends / Holidays / Time zone). Pure, deterministic, and
/// side-effect free by design — takes a policy snapshot and a candidate UTC instant, returns the
/// next UTC instant that satisfies every constraint. No DB/IO here so it can be exhaustively unit
/// tested without a fake clock or fake mailbox, the same separation-of-concerns discipline used for
/// CaseMatchingService/ReplyMatchingService in earlier phases.
/// </summary>
public static class ReminderTimingCalculator
{
    private const int MaxAdjustmentIterations = 3650; // ~10 years of daily steps — a safety bound, not an expected path.

    /// <summary>
    /// Given a raw candidate due time, returns the next time at/after it that falls within business
    /// hours (if restricted), is not a weekend (if excluded), and is not a configured holiday. If
    /// the candidate already satisfies every constraint, it is returned unchanged.
    /// </summary>
    public static DateTimeOffset AdjustToAllowedWindow(ReminderPolicy policy, DateTimeOffset candidateUtc, IReadOnlyCollection<DateOnly> holidayDates)
    {
        var tz = ResolveTimeZone(policy.TimeZoneId);
        var current = candidateUtc;

        for (var i = 0; i < MaxAdjustmentIterations; i++)
        {
            var local = TimeZoneInfo.ConvertTime(current, tz);

            if (policy.ExcludeWeekends && IsWeekend(local))
            {
                current = NextMidnight(local, tz);
                continue;
            }

            if (holidayDates.Contains(DateOnly.FromDateTime(local.Date)))
            {
                current = NextMidnight(local, tz);
                continue;
            }

            if (policy.RestrictToBusinessHours)
            {
                var timeOfDay = local.TimeOfDay;
                if (timeOfDay < policy.BusinessHoursStart)
                {
                    current = AtTimeOfDay(local, policy.BusinessHoursStart, tz);
                    continue;
                }
                if (timeOfDay >= policy.BusinessHoursEnd)
                {
                    current = AtTimeOfDay(local, policy.BusinessHoursStart, tz).AddDays(1);
                    continue;
                }
            }

            return current;
        }

        // Exhausted the safety bound (e.g. a misconfigured policy with every day marked a
        // holiday) — return the original candidate rather than loop forever or throw; the caller
        // (scheduling service) still records why via the normal CaseEvent/Reminder audit trail.
        return candidateUtc;
    }

    public static bool IsWeekend(DateTimeOffset local) =>
        local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    private static DateTimeOffset NextMidnight(DateTimeOffset local, TimeZoneInfo tz)
    {
        var nextLocalDate = local.Date.AddDays(1);
        return LocalToUtc(nextLocalDate, TimeSpan.Zero, tz);
    }

    private static DateTimeOffset AtTimeOfDay(DateTimeOffset local, TimeSpan timeOfDay, TimeZoneInfo tz)
    {
        return LocalToUtc(local.Date, timeOfDay, tz);
    }

    private static DateTimeOffset LocalToUtc(DateTime localDate, TimeSpan timeOfDay, TimeZoneInfo tz)
    {
        var unspecified = DateTime.SpecifyKind(localDate.Date + timeOfDay, DateTimeKind.Unspecified);
        var offset = tz.GetUtcOffset(unspecified);
        return new DateTimeOffset(unspecified, offset).ToUniversalTime();
    }

    /// <summary>Falls back to UTC for an unknown/invalid time zone id rather than throwing — a bad CMS-entered id must not take down the whole reminder engine (§54/§20 non-fatal processing principle carried over from Phases 3-4).</summary>
    public static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)) return TimeZoneInfo.Utc;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
