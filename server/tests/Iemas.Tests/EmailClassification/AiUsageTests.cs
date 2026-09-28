using System.Net;
using System.Text;
using Iemas.Application.AiUsage;
using Iemas.Application.Common.Ai;
using Iemas.Domain.Ai;
using Iemas.Infrastructure.Ai;
using Iemas.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Iemas.Tests.EmailClassification;

public class AiUsageTests
{
    private sealed class CapturingRecorder : IAiUsageRecorder
    {
        public List<AiUsageRecord> Records { get; } = new();

        public Task RecordAsync(AiUsageRecord record, CancellationToken cancellationToken)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private static readonly ClassificationRequest Request = new(
        "Price request", "Please quote 20 units.", "c@example.com", "sales@sawo.com", false, "Sales", "PRICE_REQUEST", "price", "newsletter",
        EmailMessageId: Guid.NewGuid());

    [Fact]
    public async Task Wrapper_RecordsEveryCall_IncludingFailures()
    {
        var recorder = new CapturingRecorder();
        var inner = new FakeAiClassificationProvider();
        var wrapper = new UsageRecordingAiClassificationProvider(inner, recorder);

        inner.Behavior = (_, model, _, _) => Task.FromResult(new ClassificationAttemptResult(
            true, "OpenRouter", model, new ClassificationResponse(true, "PRICE_REQUEST", true, true, "HIGH", 0.9, "s"), null, 120,
            Usage: new AiTokenUsage(400, 60, 460, 0.000096m, "gen-1")));
        await wrapper.ClassifyAsync(Request, "openai/gpt-4o-mini", TimeSpan.FromSeconds(5), CancellationToken.None);

        inner.Behavior = (_, model, _, _) => Task.FromResult(new ClassificationAttemptResult(
            false, "OpenRouter", model, null, "OpenRouter returned HTTP 429", 30, ClassificationFailureCategory.RateLimited));
        var failed = await wrapper.ClassifyAsync(Request, "anthropic/claude-haiku", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(failed.Succeeded);
        Assert.Equal(2, recorder.Records.Count);
        var ok = recorder.Records[0];
        Assert.True(ok.Succeeded);
        Assert.Equal(460, ok.TotalTokens);
        Assert.Equal(0.000096m, ok.CostUsd);
        Assert.Equal(AiUsagePurpose.EmailClassification, ok.Purpose);
        Assert.Equal(Request.EmailMessageId, ok.EmailMessageId);
        Assert.False(recorder.Records[1].Succeeded);
        Assert.Null(recorder.Records[1].CostUsd);
    }

    [Fact]
    public async Task Provider_RequestsUsageAndParsesCostFromResponse()
    {
        string? body = null;
        var handler = new FakeHttpMessageHandler
        {
            Behavior = async (req, _) =>
            {
                body = await req.Content!.ReadAsStringAsync();
                const string json = """
                    {"id":"gen-abc","choices":[{"message":{"content":"{\"relevant\":true,\"category\":\"PRICE_REQUEST\",\"action_required\":true,\"response_expected\":true,\"priority\":\"HIGH\",\"confidence\":0.9,\"summary\":\"x\"}"}}],
                     "usage":{"prompt_tokens":410,"completion_tokens":55,"total_tokens":465,"cost":0.0000945}}
                    """;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            },
        };
        var provider = new OpenRouterClassificationProvider(new HttpClient(handler), new StaticAiProviderConnectionResolver("sk-test-key"),
            Options.Create(new AiClassificationOptions()), NullLogger<OpenRouterClassificationProvider>.Instance);

        var result = await provider.ClassifyAsync(Request, "openai/gpt-4o-mini", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains("\"usage\":{\"include\":true}", body);
        Assert.Equal(new AiTokenUsage(410, 55, 465, 0.0000945m, "gen-abc"), result.Usage);
    }

    /// <summary>Regression: OpenRouter reports "limit": null for keys without a spending limit; that must not crash.</summary>
    [Fact]
    public async Task AdminClient_KeyWithoutSpendingLimit_ReportsUsage()
    {
        var handler = new FakeHttpMessageHandler
        {
            Behavior = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":{"label":"sk-or-v1-abc...","usage":0.0123,"limit":null,"limit_remaining":null}}""", Encoding.UTF8, "application/json"),
            }),
        };
        var client = new OpenRouterAdminClient(new HttpClient(handler), new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            NullLogger<OpenRouterAdminClient>.Instance);

        var result = await client.CheckKeyAsync(new Iemas.Application.AiModels.AiProviderConnection("https://openrouter.ai/api/v1", "sk-test-key", "CMS"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0.0123m, result.UsageUsd);
        Assert.Null(result.LimitUsd);
        Assert.Null(result.LimitRemainingUsd);
    }

    [Fact]
    public async Task GetCallsAsync_TenPerPage_WithTotals()
    {
        using var db = TestDbContext.CreateNew();
        for (var i = 0; i < 23; i++)
        {
            db.AiUsageRecords.Add(new AiUsageRecord
            {
                Provider = "OpenRouter", ModelIdentifier = i % 2 == 0 ? "openai/gpt-4o-mini" : "anthropic/claude-haiku",
                Purpose = AiUsagePurpose.EmailClassification, Succeeded = i != 5, TotalTokens = 100, CostUsd = 0.001m,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-i),
            });
        }
        await db.SaveChangesAsync();
        var service = new AiUsageQueryService(db);

        var first = await service.GetCallsAsync(1, 10, null, null, null, null, null, CancellationToken.None);
        var last = await service.GetCallsAsync(3, 10, null, null, null, null, null, CancellationToken.None);
        var beyond = await service.GetCallsAsync(99, 10, null, null, null, null, null, CancellationToken.None);
        var filtered = await service.GetCallsAsync(1, 10, "anthropic/claude-haiku", null, false, null, null, CancellationToken.None);

        Assert.Equal(10, first.Items.Count);
        Assert.Equal(23, first.TotalCount);
        Assert.Equal(3, first.TotalPages);
        Assert.True(first.Items[0].OccurredAt > first.Items[9].OccurredAt);
        Assert.Equal(3, last.Items.Count);
        Assert.Equal(3, beyond.Page);
        Assert.Single(filtered.Items);
    }

    [Fact]
    public async Task GetSummaryAsync_SumsCostTokensAndFailures()
    {
        using var db = TestDbContext.CreateNew();
        db.AiUsageRecords.AddRange(
            new AiUsageRecord { Provider = "OpenRouter", ModelIdentifier = "a", Purpose = "EmailClassification", Succeeded = true, TotalTokens = 500, CostUsd = 0.002m, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1) },
            new AiUsageRecord { Provider = "OpenRouter", ModelIdentifier = "a", Purpose = "EmailClassification", Succeeded = false, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2) },
            new AiUsageRecord { Provider = "OpenRouter", ModelIdentifier = "b", Purpose = "ModelTest", Succeeded = true, TotalTokens = 100, CostUsd = 0.0005m, CreatedAt = DateTimeOffset.UtcNow.AddDays(-20) },
            new AiUsageRecord { Provider = "OpenRouter", ModelIdentifier = "b", Purpose = "ModelTest", Succeeded = true, TotalTokens = 100, CostUsd = 1m, CreatedAt = DateTimeOffset.UtcNow.AddDays(-45) });
        await db.SaveChangesAsync();

        var summary = await new AiUsageQueryService(db).GetSummaryAsync(CancellationToken.None);

        var week = summary.Periods.Single(p => p.Label == "Last 7 days");
        Assert.Equal(2, week.Calls);
        Assert.Equal(1, week.FailedCalls);
        Assert.Equal(0.002m, week.CostUsd);
        Assert.Equal(1, week.CallsWithoutCost);
        var month30 = summary.Periods.Single(p => p.Label == "Last 30 days");
        Assert.Equal(3, month30.Calls);
        Assert.Equal(0.0025m, month30.CostUsd);
        Assert.Equal("a", summary.ByModelLast30Days[0].ModelIdentifier);
        Assert.Equal(2, summary.ByModelLast30Days.Count);
    }
}
