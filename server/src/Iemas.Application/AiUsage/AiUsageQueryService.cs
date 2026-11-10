using Iemas.Application.Common;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Operations;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.AiUsage;

public record AiUsagePeriodDto(string Label, int Calls, int FailedCalls, long Tokens, decimal CostUsd, int CallsWithoutCost);

public record AiUsageByModelDto(string ModelIdentifier, int Calls, int FailedCalls, long Tokens, decimal CostUsd, decimal AverageCostPerCall);

public record AiUsageSummaryDto(string TimeZone, List<AiUsagePeriodDto> Periods, List<AiUsageByModelDto> ByModelLast30Days);

public record AiUsageCallDto(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Provider,
    string ModelIdentifier,
    string Purpose,
    Guid? EmailMessageId,
    string? EmailSubject,
    bool Succeeded,
    string? ErrorMessage,
    long DurationMs,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    decimal? CostUsd);

/// <summary>Everything over the chosen range, all calls included (retries, fallbacks, failures, tests).</summary>
public record AiAnalyticsTotalsDto(
    int Calls, int SucceededCalls, int FailedCalls,
    long PromptTokens, long CompletionTokens, long TotalTokens,
    decimal CostUsd,
    /// <summary>Spend on calls that failed: money that bought no classification.</summary>
    decimal FailedCostUsd,
    int CallsWithoutCost,
    /// <summary>Average over the calls the provider reported a cost for.</summary>
    decimal AverageCostPerCallUsd,
    double AverageTokensPerCall, double AverageDurationMs);

/// <summary>One local calendar day, zero-filled so the chart has no gaps.</summary>
public record AiAnalyticsDayDto(string Date, int Calls, int FailedCalls, long PromptTokens, long CompletionTokens, decimal CostUsd);

public record AiAnalyticsModelDto(
    string ModelIdentifier, int Calls, int FailedCalls, long PromptTokens, long CompletionTokens,
    decimal CostUsd, decimal AverageCostPerCallUsd, double SharePercent);

public record AiAnalyticsPurposeDto(string Purpose, int Calls, long Tokens, decimal CostUsd, double SharePercent);

/// <summary>How much of the email stream the built-in (no-AI) filter handled, and what that is worth.</summary>
public record AiAnalyticsEfficiencyDto(
    int EmailsClassified, int EmailsWithAi, int EmailsWithoutAi, double WithoutAiPercent,
    /// <summary>All classification spend divided by all emails classified, per 1,000 emails.</summary>
    decimal CostPer1000EmailsUsd,
    decimal CostPerAiEmailUsd,
    /// <summary>What the emails that never needed AI would have cost at the average AI-email price.</summary>
    decimal EstimatedSavedUsd);

/// <param name="DailyAverageLast7DaysUsd">Running cost per day (email classification + resolution checks) over the last 7 days; one-off tests and model analysis are excluded.</param>
public record AiAnalyticsForecastDto(
    decimal MonthToDateUsd, decimal DailyAverageLast7DaysUsd, int DaysInMonth, int DayOfMonth, decimal ProjectedMonthUsd);

public record AiAnalyticsDto(
    int Days, string From, string To, string TimeZone,
    AiAnalyticsTotalsDto Totals,
    List<AiAnalyticsDayDto> Daily,
    List<AiAnalyticsModelDto> ByModel,
    List<AiAnalyticsPurposeDto> ByPurpose,
    AiAnalyticsEfficiencyDto Efficiency,
    AiAnalyticsForecastDto Forecast);

/// <summary>Read-only cost monitoring over recorded AI provider calls.</summary>
public class AiUsageQueryService
{
    public const int DefaultPageSize = 10;

    private readonly IAppDbContext _db;

    public AiUsageQueryService(IAppDbContext db)
    {
        _db = db;
    }

    public async Task<AiUsageSummaryDto> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var zone = await SystemSettingsService.GetDefaultTimeZoneAsync(_db, cancellationToken);
        var nowUtc = DateTimeOffset.UtcNow;
        var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, zone);
        var startOfToday = StartOfLocalDay(nowLocal.Date, zone);
        var startOfMonth = StartOfLocalDay(new DateTime(nowLocal.Year, nowLocal.Month, 1), zone);

        var windows = new (string Label, DateTimeOffset From)[]
        {
            ("Today", startOfToday),
            ("Last 7 days", nowUtc.AddDays(-7)),
            ("Last 30 days", nowUtc.AddDays(-30)),
            ("This month", startOfMonth),
        };
        var earliest = windows.Min(w => w.From);

        var rows = await _db.AiUsageRecords.AsNoTracking()
            .Where(r => r.CreatedAt >= earliest)
            .Select(r => new { r.CreatedAt, r.ModelIdentifier, r.Succeeded, r.TotalTokens, r.CostUsd })
            .ToListAsync(cancellationToken);

        var periods = windows.Select(w =>
        {
            var inWindow = rows.Where(r => r.CreatedAt >= w.From).ToList();
            return new AiUsagePeriodDto(
                w.Label, inWindow.Count, inWindow.Count(r => !r.Succeeded),
                inWindow.Sum(r => (long)(r.TotalTokens ?? 0)), inWindow.Sum(r => r.CostUsd ?? 0m),
                inWindow.Count(r => r.CostUsd is null));
        }).ToList();

        var last30 = nowUtc.AddDays(-30);
        var byModel = rows.Where(r => r.CreatedAt >= last30)
            .GroupBy(r => r.ModelIdentifier)
            .Select(g =>
            {
                var cost = g.Sum(r => r.CostUsd ?? 0m);
                return new AiUsageByModelDto(g.Key, g.Count(), g.Count(r => !r.Succeeded), g.Sum(r => (long)(r.TotalTokens ?? 0)), cost,
                    g.Count() == 0 ? 0 : Math.Round(cost / g.Count(), 8));
            })
            .OrderByDescending(m => m.CostUsd)
            .ThenByDescending(m => m.Calls)
            .ToList();

        return new AiUsageSummaryDto(zone.Id, periods, byModel);
    }

    public const int MaxAnalyticsDays = 365;

    /// <summary>Calls the system makes by itself as it runs; everything else is a one-off check an admin started.</summary>
    private static readonly HashSet<string> RunningPurposes = new() { "EmailClassification", "ResolutionAssessment" };

    /// <summary>
    /// Spend and token analytics over the last <paramref name="days"/> local days (today included): totals, a
    /// zero-filled daily series, breakdowns by model and by purpose, how much the no-AI filter saves, and a
    /// month-end projection. Days are local to the configured time zone (System Settings).
    /// </summary>
    public async Task<AiAnalyticsDto> GetAnalyticsAsync(int days, CancellationToken cancellationToken)
    {
        days = Math.Clamp(days, 1, MaxAnalyticsDays);
        var zone = await SystemSettingsService.GetDefaultTimeZoneAsync(_db, cancellationToken);
        var nowUtc = DateTimeOffset.UtcNow;
        var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, zone);
        var today = nowLocal.Date;
        var firstDay = today.AddDays(-(days - 1));
        var from = StartOfLocalDay(firstDay, zone);
        var startOfMonth = StartOfLocalDay(new DateTime(today.Year, today.Month, 1), zone);
        var last7From = nowUtc.AddDays(-7);

        var earliest = new[] { from, startOfMonth, last7From }.Min();
        var all = await _db.AiUsageRecords.AsNoTracking()
            .Where(r => r.CreatedAt >= earliest)
            .Select(r => new
            {
                r.CreatedAt, r.ModelIdentifier, r.Purpose, r.Succeeded, r.EmailMessageId, r.DurationMs,
                r.PromptTokens, r.CompletionTokens, r.TotalTokens, r.CostUsd,
            })
            .ToListAsync(cancellationToken);
        var rows = all.Where(r => r.CreatedAt >= from).ToList();

        long Prompt(int? p, int? total, int? completion) => p ?? Math.Max(0, (total ?? 0) - (completion ?? 0));
        var withCost = rows.Count(r => r.CostUsd is not null);
        var totalCost = rows.Sum(r => r.CostUsd ?? 0m);
        var totals = new AiAnalyticsTotalsDto(
            rows.Count, rows.Count(r => r.Succeeded), rows.Count(r => !r.Succeeded),
            rows.Sum(r => Prompt(r.PromptTokens, r.TotalTokens, r.CompletionTokens)),
            rows.Sum(r => (long)(r.CompletionTokens ?? 0)),
            rows.Sum(r => (long)(r.TotalTokens ?? 0)),
            totalCost,
            rows.Where(r => !r.Succeeded).Sum(r => r.CostUsd ?? 0m),
            rows.Count - withCost,
            withCost == 0 ? 0m : Math.Round(totalCost / withCost, 8),
            rows.Count == 0 ? 0 : Math.Round(rows.Average(r => (double)(r.TotalTokens ?? 0)), 1),
            rows.Count == 0 ? 0 : Math.Round(rows.Average(r => (double)r.DurationMs), 0));

        var byDay = rows
            .GroupBy(r => TimeZoneInfo.ConvertTime(r.CreatedAt, zone).Date)
            .ToDictionary(g => g.Key, g => g.ToList());
        var daily = Enumerable.Range(0, days).Select(i =>
        {
            var day = firstDay.AddDays(i);
            var list = byDay.GetValueOrDefault(day) ?? new();
            return new AiAnalyticsDayDto(
                day.ToString("yyyy-MM-dd"), list.Count, list.Count(r => !r.Succeeded),
                list.Sum(r => Prompt(r.PromptTokens, r.TotalTokens, r.CompletionTokens)),
                list.Sum(r => (long)(r.CompletionTokens ?? 0)),
                list.Sum(r => r.CostUsd ?? 0m));
        }).ToList();

        double Share(decimal part, decimal whole, int partCalls, int wholeCalls) =>
            whole > 0 ? Math.Round((double)(part / whole) * 100, 1) : wholeCalls > 0 ? Math.Round(partCalls * 100.0 / wholeCalls, 1) : 0;

        var byModel = rows.GroupBy(r => r.ModelIdentifier)
            .Select(g =>
            {
                var cost = g.Sum(r => r.CostUsd ?? 0m);
                var costed = g.Count(r => r.CostUsd is not null);
                return new AiAnalyticsModelDto(
                    g.Key, g.Count(), g.Count(r => !r.Succeeded),
                    g.Sum(r => Prompt(r.PromptTokens, r.TotalTokens, r.CompletionTokens)), g.Sum(r => (long)(r.CompletionTokens ?? 0)),
                    cost, costed == 0 ? 0m : Math.Round(cost / costed, 8), Share(cost, totalCost, g.Count(), rows.Count));
            })
            .OrderByDescending(m => m.CostUsd).ThenByDescending(m => m.Calls).ToList();

        var byPurpose = rows.GroupBy(r => r.Purpose)
            .Select(g =>
            {
                var cost = g.Sum(r => r.CostUsd ?? 0m);
                return new AiAnalyticsPurposeDto(g.Key, g.Count(), g.Sum(r => (long)(r.TotalTokens ?? 0)), cost, Share(cost, totalCost, g.Count(), rows.Count));
            })
            .OrderByDescending(p => p.CostUsd).ThenByDescending(p => p.Calls).ToList();

        // How many emails never needed AI: every email classified in the range, minus those that had at least one AI call.
        var emailsClassified = await _db.EmailClassifications.AsNoTracking().CountAsync(c => c.ClassifiedAt >= from, cancellationToken);
        var classification = rows.Where(r => r.Purpose == "EmailClassification").ToList();
        var emailsWithAi = classification.Where(r => r.EmailMessageId != null).Select(r => r.EmailMessageId).Distinct().Count();
        var emailsWithoutAi = Math.Max(0, emailsClassified - emailsWithAi);
        var classificationCost = classification.Sum(r => r.CostUsd ?? 0m);
        var costPerAiEmail = emailsWithAi == 0 ? 0m : Math.Round(classificationCost / emailsWithAi, 8);
        var efficiency = new AiAnalyticsEfficiencyDto(
            emailsClassified, Math.Min(emailsWithAi, emailsClassified), emailsWithoutAi,
            emailsClassified == 0 ? 0 : Math.Round(emailsWithoutAi * 100.0 / emailsClassified, 1),
            emailsClassified == 0 ? 0m : Math.Round(classificationCost / emailsClassified * 1000m, 4),
            costPerAiEmail,
            Math.Round(emailsWithoutAi * costPerAiEmail, 6));

        // Month-end projection: everything spent so far this month, plus what the RUNNING work (classifying email,
        // checking resolution) costs per day, over the time left. One-off checks the admin started (model tests,
        // model analysis) are already in "so far" but do not repeat, so they are left out of the daily rate.
        var monthToDate = all.Where(r => r.CreatedAt >= startOfMonth).Sum(r => r.CostUsd ?? 0m);
        var dailyAverage = all.Where(r => r.CreatedAt >= last7From && RunningPurposes.Contains(r.Purpose)).Sum(r => r.CostUsd ?? 0m) / 7m;
        var daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
        var remainingDays = (decimal)(daysInMonth - today.Day) + (decimal)((24 - nowLocal.TimeOfDay.TotalHours) / 24.0);
        var forecast = new AiAnalyticsForecastDto(
            monthToDate, Math.Round(dailyAverage, 6), daysInMonth, today.Day, Math.Round(monthToDate + dailyAverage * remainingDays, 6));

        return new AiAnalyticsDto(days, firstDay.ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd"), zone.Id,
            totals, daily, byModel, byPurpose, efficiency, forecast);
    }

    public async Task<PagedResult<AiUsageCallDto>> GetCallsAsync(
        int page, int pageSize, string? model, string? purpose, bool? succeeded, DateTimeOffset? from, DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(page, 1);

        var query = _db.AiUsageRecords.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(model)) query = query.Where(r => r.ModelIdentifier == model);
        if (!string.IsNullOrWhiteSpace(purpose)) query = query.Where(r => r.Purpose == purpose);
        if (succeeded is bool ok) query = query.Where(r => r.Succeeded == ok);
        if (from is DateTimeOffset fromValue) query = query.Where(r => r.CreatedAt >= fromValue);
        if (to is DateTimeOffset toValue) query = query.Where(r => r.CreatedAt <= toValue);

        var total = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        page = Math.Min(page, totalPages);

        var items = await query
            .OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new AiUsageCallDto(
                r.Id, r.CreatedAt, r.Provider, r.ModelIdentifier, r.Purpose, r.EmailMessageId,
                r.EmailMessageId == null ? null : _db.EmailMessages.Where(m => m.Id == r.EmailMessageId).Select(m => m.Subject).FirstOrDefault(),
                r.Succeeded, r.ErrorMessage, r.DurationMs, r.PromptTokens, r.CompletionTokens, r.TotalTokens, r.CostUsd))
            .ToListAsync(cancellationToken);

        return new PagedResult<AiUsageCallDto>(items, total, page, pageSize, totalPages);
    }

    public async Task<List<string>> GetModelsUsedAsync(CancellationToken cancellationToken) =>
        await _db.AiUsageRecords.AsNoTracking().Select(r => r.ModelIdentifier).Distinct().OrderBy(m => m).ToListAsync(cancellationToken);

    private static DateTimeOffset StartOfLocalDay(DateTime localDate, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified)).ToUniversalTime();
    }
}
