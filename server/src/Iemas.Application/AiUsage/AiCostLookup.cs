using Iemas.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.AiUsage;

/// <summary>What AI spent on one thing (an email, or an inquiry): every call counts, including retries and failures.</summary>
public record AiCostDto(int Calls, int FailedCalls, long PromptTokens, long CompletionTokens, long TotalTokens, decimal CostUsd, int CallsWithoutCost);

/// <summary>An inquiry's AI spend, split into checking its emails and judging whether it looks resolved.</summary>
public record CaseAiCostDto(AiCostDto Total, AiCostDto Classification, AiCostDto ResolutionChecks);

/// <summary>
/// Looks up AI cost per email / per inquiry from the recorded provider calls (one query per page, never per row).
/// An email with no recorded call returns <see cref="Empty"/>: the built-in filter settled it for free.
/// </summary>
public static class AiCostLookup
{
    public static readonly AiCostDto Empty = new(0, 0, 0, 0, 0, 0m, 0);

    private readonly record struct Row(bool Succeeded, int? Prompt, int? Completion, int? Total, decimal? Cost);

    public static AiCostDto Combine(params AiCostDto[] parts) => new(
        parts.Sum(p => p.Calls), parts.Sum(p => p.FailedCalls), parts.Sum(p => p.PromptTokens), parts.Sum(p => p.CompletionTokens),
        parts.Sum(p => p.TotalTokens), parts.Sum(p => p.CostUsd), parts.Sum(p => p.CallsWithoutCost));

    private static AiCostDto Summarize(IEnumerable<Row> rows)
    {
        var list = rows.ToList();
        return new AiCostDto(
            list.Count, list.Count(r => !r.Succeeded),
            list.Sum(r => (long)(r.Prompt ?? Math.Max(0, (r.Total ?? 0) - (r.Completion ?? 0)))),
            list.Sum(r => (long)(r.Completion ?? 0)),
            list.Sum(r => (long)(r.Total ?? 0)),
            list.Sum(r => r.Cost ?? 0m),
            list.Count(r => r.Cost is null));
    }

    /// <summary>AI cost for each of the given emails. Emails the AI never saw are absent from the result.</summary>
    public static async Task<Dictionary<Guid, AiCostDto>> ForEmailsAsync(IAppDbContext db, IReadOnlyCollection<Guid> emailIds, CancellationToken cancellationToken)
    {
        if (emailIds.Count == 0) return new Dictionary<Guid, AiCostDto>();
        var rows = await db.AiUsageRecords.AsNoTracking()
            .Where(r => r.EmailMessageId != null && emailIds.Contains(r.EmailMessageId.Value))
            .Select(r => new { Email = r.EmailMessageId!.Value, r.Succeeded, r.PromptTokens, r.CompletionTokens, r.TotalTokens, r.CostUsd })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.Email)
            .ToDictionary(g => g.Key, g => Summarize(g.Select(r => new Row(r.Succeeded, r.PromptTokens, r.CompletionTokens, r.TotalTokens, r.CostUsd))));
    }

    /// <summary>
    /// An inquiry's total: the cost of checking its emails (already looked up per email) plus the resolution checks the
    /// AI ran on the inquiry as a whole.
    /// </summary>
    public static async Task<CaseAiCostDto> ForCaseAsync(IAppDbContext db, Guid caseId, IReadOnlyDictionary<Guid, AiCostDto> emailCosts, CancellationToken cancellationToken)
    {
        var resolutionRows = await db.AiUsageRecords.AsNoTracking()
            .Where(r => r.CaseId == caseId)
            .Select(r => new Row(r.Succeeded, r.PromptTokens, r.CompletionTokens, r.TotalTokens, r.CostUsd))
            .ToListAsync(cancellationToken);
        var classification = Combine(emailCosts.Values.ToArray());
        var resolution = Summarize(resolutionRows);
        return new CaseAiCostDto(Combine(classification, resolution), classification, resolution);
    }
}
