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
