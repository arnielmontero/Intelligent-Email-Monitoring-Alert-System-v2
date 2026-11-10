using Iemas.Application.AiUsage;
using Iemas.Domain.Ai;
using Iemas.Domain.Email;
using Iemas.Tests.TestSupport;

namespace Iemas.Tests.EmailClassification;

/// <summary>AI spend analytics: totals, a zero-filled daily series, breakdowns, the no-AI saving and the month-end projection.</summary>
public class AiAnalyticsTests
{
    private static AiUsageRecord Call(string model, string purpose, bool ok, int? prompt, int? completion, decimal? cost, DateTimeOffset at, Guid? email = null, long ms = 1000) =>
        new()
        {
            Provider = "OpenRouter", ModelIdentifier = model, Purpose = purpose, Succeeded = ok, EmailMessageId = email, DurationMs = ms,
            PromptTokens = prompt, CompletionTokens = completion, TotalTokens = prompt is null ? null : prompt + (completion ?? 0), CostUsd = cost, CreatedAt = at,
        };

    [Fact]
    public async Task Totals_SplitTokensInAndOut_AndCountFailedSpendAsWasted()
    {
        using var db = TestDbContext.CreateNew();
        var now = DateTimeOffset.UtcNow;
        db.AiUsageRecords.AddRange(
            Call("a", "EmailClassification", true, 400, 100, 0.001m, now.AddMinutes(-5)),
            Call("a", "EmailClassification", true, 600, 50, 0.002m, now.AddMinutes(-6)),
            Call("a", "EmailClassification", false, 300, 0, 0.0005m, now.AddMinutes(-7)),
            Call("b", "ResolutionAssessment", true, null, null, null, now.AddMinutes(-8)));
        await db.SaveChangesAsync();

        var t = (await new AiUsageQueryService(db).GetAnalyticsAsync(7, CancellationToken.None)).Totals;

        Assert.Equal(4, t.Calls);
        Assert.Equal(1, t.FailedCalls);
        Assert.Equal(1300, t.PromptTokens);
        Assert.Equal(150, t.CompletionTokens);
        Assert.Equal(1450, t.TotalTokens);
        Assert.Equal(0.0035m, t.CostUsd);
        Assert.Equal(0.0005m, t.FailedCostUsd);
        Assert.Equal(1, t.CallsWithoutCost);
        Assert.Equal(Math.Round(0.0035m / 3, 8), t.AverageCostPerCallUsd);   // over the calls that reported a cost
    }

    [Fact]
    public async Task Daily_IsZeroFilled_OneEntryPerDay_NewestLast_AndOutsideTheRangeIsIgnored()
    {
        using var db = TestDbContext.CreateNew();
        var now = DateTimeOffset.UtcNow;
        db.AiUsageRecords.AddRange(
            Call("a", "EmailClassification", true, 100, 10, 0.01m, now.AddMinutes(-1)),
            Call("a", "EmailClassification", true, 100, 10, 0.02m, now.AddDays(-2)),
            Call("a", "EmailClassification", true, 100, 10, 5m, now.AddDays(-40)));
        await db.SaveChangesAsync();

        var result = await new AiUsageQueryService(db).GetAnalyticsAsync(7, CancellationToken.None);

        Assert.Equal(7, result.Daily.Count);
        Assert.Equal(result.To, result.Daily[^1].Date);
        Assert.Equal(result.From, result.Daily[0].Date);
        Assert.Equal(0.03m, result.Daily.Sum(d => d.CostUsd));          // the 40-day-old call is not in a 7-day range
        Assert.Equal(5, result.Daily.Count(d => d.Calls == 0));
    }

    [Fact]
    public async Task Breakdowns_AreRankedByCost_WithSharesThatSumToAbout100()
    {
        using var db = TestDbContext.CreateNew();
        var now = DateTimeOffset.UtcNow;
        db.AiUsageRecords.AddRange(
            Call("cheap", "EmailClassification", true, 100, 10, 0.001m, now.AddHours(-1)),
            Call("pricey", "EmailClassification", true, 100, 10, 0.003m, now.AddHours(-2)),
            Call("pricey", "ModelBenchmark", true, 100, 10, 0.006m, now.AddHours(-3)));
        await db.SaveChangesAsync();

        var result = await new AiUsageQueryService(db).GetAnalyticsAsync(30, CancellationToken.None);

        Assert.Equal(new[] { "pricey", "cheap" }, result.ByModel.Select(m => m.ModelIdentifier));
        Assert.Equal(90.0, result.ByModel[0].SharePercent);
        Assert.Equal(new[] { "ModelBenchmark", "EmailClassification" }, result.ByPurpose.Select(p => p.Purpose));
        Assert.InRange(result.ByPurpose.Sum(p => p.SharePercent), 99.9, 100.1);
    }

    [Fact]
    public async Task Efficiency_CountsEmailsThatNeverNeededAi_AndWhatThatSaved()
    {
        using var db = TestDbContext.CreateNew();
        var account = new EmailAccount
        {
            EmailAddress = "sales@sawo.com", Purpose = EmailAccountPurpose.Inbound, Protocol = EmailProtocol.Imap, Host = "h", Port = 993,
            Username = "u", AuthMethod = EmailAuthMethod.Password, IsActive = true, MonitoringEnabled = true,
        };
        db.EmailAccounts.Add(account);
        var messages = Enumerable.Range(0, 10).Select(i => new EmailMessage
        {
            EmailAccountId = account.Id, Provider = EmailProtocol.Imap, ProviderMessageId = Guid.NewGuid().ToString(), FromAddress = "c@example.com",
            ToAddresses = "sales@sawo.com", Subject = $"m{i}", ReceivedAt = DateTimeOffset.UtcNow, ProcessingStatus = EmailProcessingStatus.Processed,
        }).ToList();
        db.EmailMessages.AddRange(messages);
        await db.SaveChangesAsync();
        db.EmailClassifications.AddRange(messages.Select(m => new Iemas.Domain.Ai.EmailClassification { EmailMessageId = m.Id, Decision = ImportanceDecision.Important }));

        // 2 of the 10 emails needed AI; one of them was retried (2 calls), so cost per AI email = (0.001 + 0.001 + 0.002) / 2.
        var now = DateTimeOffset.UtcNow;
        db.AiUsageRecords.AddRange(
            Call("a", "EmailClassification", false, 100, 0, 0.001m, now.AddMinutes(-3), messages[0].Id),
            Call("a", "EmailClassification", true, 100, 10, 0.001m, now.AddMinutes(-2), messages[0].Id),
            Call("a", "EmailClassification", true, 100, 10, 0.002m, now.AddMinutes(-1), messages[1].Id));
        await db.SaveChangesAsync();

        var e = (await new AiUsageQueryService(db).GetAnalyticsAsync(30, CancellationToken.None)).Efficiency;

        Assert.Equal(10, e.EmailsClassified);
        Assert.Equal(2, e.EmailsWithAi);
        Assert.Equal(8, e.EmailsWithoutAi);
        Assert.Equal(80.0, e.WithoutAiPercent);
        Assert.Equal(0.002m, e.CostPerAiEmailUsd);
        Assert.Equal(0.016m, e.EstimatedSavedUsd);        // 8 emails x $0.002
        Assert.Equal(0.4m, e.CostPer1000EmailsUsd);       // $0.004 over 10 emails, per 1,000
    }

    [Fact]
    public async Task Forecast_ProjectsTheMonthFromTheLastSevenDays()
    {
        using var db = TestDbContext.CreateNew();
        var now = DateTimeOffset.UtcNow;
        db.AiUsageRecords.Add(Call("a", "EmailClassification", true, 100, 10, 0.07m, now.AddMinutes(-1)));
        await db.SaveChangesAsync();

        var f = (await new AiUsageQueryService(db).GetAnalyticsAsync(30, CancellationToken.None)).Forecast;

        Assert.Equal(0.07m, f.MonthToDateUsd);
        Assert.Equal(0.01m, f.DailyAverageLast7DaysUsd);                 // $0.07 over 7 days
        Assert.True(f.ProjectedMonthUsd >= f.MonthToDateUsd);
        Assert.InRange(f.ProjectedMonthUsd, 0.07m, 0.07m + 0.01m * f.DaysInMonth);
    }

    [Fact]
    public async Task Forecast_DailyRateIgnoresOneOffTestsAndModelAnalysis_ButMonthToDateCountsThem()
    {
        using var db = TestDbContext.CreateNew();
        var now = DateTimeOffset.UtcNow;
        db.AiUsageRecords.AddRange(
            Call("a", "EmailClassification", true, 100, 10, 0.007m, now.AddHours(-1)),
            Call("a", "ResolutionAssessment", true, 100, 10, 0.007m, now.AddHours(-2)),
            Call("b", "ModelBenchmark", true, 100, 10, 5m, now.AddHours(-3)),     // an admin-started analysis run
            Call("b", "ModelTest", true, 100, 10, 1m, now.AddHours(-4)));
        await db.SaveChangesAsync();

        var f = (await new AiUsageQueryService(db).GetAnalyticsAsync(30, CancellationToken.None)).Forecast;

        Assert.Equal(6.014m, f.MonthToDateUsd);                 // already spent: everything counts
        Assert.Equal(0.002m, f.DailyAverageLast7DaysUsd);       // $0.014 of running cost over 7 days: the one-offs don't repeat
    }

    [Fact]
    public async Task NoData_ReturnsZerosNotErrors()
    {
        using var db = TestDbContext.CreateNew();

        var result = await new AiUsageQueryService(db).GetAnalyticsAsync(30, CancellationToken.None);

        Assert.Equal(30, result.Daily.Count);
        Assert.Equal(0, result.Totals.Calls);
        Assert.Equal(0m, result.Totals.AverageCostPerCallUsd);
        Assert.Empty(result.ByModel);
        Assert.Equal(0, result.Efficiency.EmailsClassified);
        Assert.Equal(0m, result.Forecast.ProjectedMonthUsd);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(9999, 365)]
    public async Task Days_IsClampedToAValidRange(int asked, int expected)
    {
        using var db = TestDbContext.CreateNew();

        var result = await new AiUsageQueryService(db).GetAnalyticsAsync(asked, CancellationToken.None);

        Assert.Equal(expected, result.Days);
        Assert.Equal(expected, result.Daily.Count);
    }
}
