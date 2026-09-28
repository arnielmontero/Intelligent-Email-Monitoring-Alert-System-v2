using Iemas.Application.AiModels;
using Iemas.Application.AiModels.Dtos;
using Iemas.Application.Common.Ai;
using Iemas.Domain.Ai;
using Iemas.Domain.Identity;
using Iemas.Infrastructure.Ai;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Iemas.Tests.EmailClassification;

public class AiProviderSettingsTests
{
    private sealed class FakeAdminClient : IAiProviderAdminClient
    {
        public AiProviderConnection? LastChecked { get; private set; }

        public Task<AiProviderKeyCheck> CheckKeyAsync(AiProviderConnection connection, CancellationToken cancellationToken)
        {
            LastChecked = connection;
            return Task.FromResult(new AiProviderKeyCheck(true, "API key is valid.", 5));
        }

        public Task<IReadOnlyList<AiCatalogModel>> GetModelsAsync(string baseUrl, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AiCatalogModel>>(new[] { new AiCatalogModel("openai/gpt-4o-mini", "GPT-4o mini", 128000, 0.15m, 0.6m) });
    }

    private static AiProviderConnectionResolver CreateResolver(TestDbContext db, string envKey = "", string envBaseUrl = "https://openrouter.ai/api/v1") =>
        new(db, new PassThroughEncryptionService(),
            Options.Create(new AiClassificationOptions { OpenRouter = new OpenRouterOptions { ApiKey = envKey, BaseUrl = envBaseUrl } }));

    private static (AiProviderSettingsService Service, NoOpAuditService Audit, FakeAdminClient Client, AiCircuitBreakerStore Breaker) CreateService(TestDbContext db, string envKey = "")
    {
        var audit = new NoOpAuditService();
        var client = new FakeAdminClient();
        var breaker = new AiCircuitBreakerStore(TimeProvider.System);
        var service = new AiProviderSettingsService(db, new PassThroughEncryptionService(), CreateResolver(db, envKey), client, breaker,
            new FakeCurrentUserService(roles: SystemRole.Administrator), audit);
        return (service, audit, client, breaker);
    }

    [Fact]
    public async Task Resolver_NothingConfigured_ReportsNoKeyAndDefaultUrl()
    {
        using var db = TestDbContext.CreateNew();
        var connection = await CreateResolver(db, envBaseUrl: "").ResolveAsync(CancellationToken.None);

        Assert.Null(connection.ApiKey);
        Assert.Equal("None", connection.KeySource);
        Assert.Equal("https://openrouter.ai/api/v1", connection.BaseUrl);
    }

    [Fact]
    public async Task Resolver_CmsKeyOverridesEnvironmentKey()
    {
        using var db = TestDbContext.CreateNew();
        var (service, _, _, _) = CreateService(db, envKey: "sk-env-key-123456");
        await service.UpdateAsync(new UpdateAiProviderSettingsRequest("https://proxy.example.com/v1/", "sk-or-cms-key-abcd", false), CancellationToken.None);

        var connection = await CreateResolver(db, envKey: "sk-env-key-123456").ResolveAsync(CancellationToken.None);

        Assert.Equal("sk-or-cms-key-abcd", connection.ApiKey);
        Assert.Equal("CMS", connection.KeySource);
        Assert.Equal("https://proxy.example.com/v1", connection.BaseUrl);
    }

    /// <summary>§16/§84 — the key is stored encrypted, never returned, and never written to the audit log.</summary>
    [Fact]
    public async Task UpdateAsync_StoresKeyWriteOnly_AndAuditsWithoutTheKey()
    {
        using var db = TestDbContext.CreateNew();
        var (service, audit, _, _) = CreateService(db);

        var result = await service.UpdateAsync(new UpdateAiProviderSettingsRequest(null, "sk-or-v1-secretvalue-9f3a", false), CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.True(result.Value!.HasApiKey);
        Assert.Equal("9f3a", result.Value.ApiKeyHint);
        Assert.Equal("CMS", result.Value.KeySource);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("AI_PROVIDER_SETTINGS_UPDATED", entry.Action);
        Assert.DoesNotContain("secretvalue", entry.Details);
        Assert.Equal("9f3a", (await db.AiProviderConfigs.SingleAsync()).ApiKeyHint);
    }

    [Fact]
    public async Task UpdateAsync_EmptyKey_KeepsExistingKey()
    {
        using var db = TestDbContext.CreateNew();
        var (service, _, _, _) = CreateService(db);
        await service.UpdateAsync(new UpdateAiProviderSettingsRequest(null, "sk-or-v1-first-key-1111", false), CancellationToken.None);

        var result = await service.UpdateAsync(new UpdateAiProviderSettingsRequest("https://openrouter.ai/api/v1", "", false), CancellationToken.None);

        Assert.Equal("1111", result.Value!.ApiKeyHint);
    }

    [Fact]
    public async Task UpdateAsync_ClearKey_FallsBackToEnvironment()
    {
        using var db = TestDbContext.CreateNew();
        var (service, _, _, _) = CreateService(db, envKey: "sk-env-key-7777");
        await service.UpdateAsync(new UpdateAiProviderSettingsRequest(null, "sk-or-v1-cms-key-2222", false), CancellationToken.None);

        var result = await service.UpdateAsync(new UpdateAiProviderSettingsRequest(null, null, true), CancellationToken.None);

        Assert.Equal("Environment", result.Value!.KeySource);
        Assert.Equal("7777", result.Value.ApiKeyHint);
    }

    [Theory]
    [InlineData("openrouter.ai/api/v1", null)]
    [InlineData("ftp://openrouter.ai", null)]
    [InlineData(null, "short")]
    [InlineData(null, "has a space in it")]
    public async Task UpdateAsync_InvalidInput_IsRejected(string? baseUrl, string? apiKey)
    {
        using var db = TestDbContext.CreateNew();
        var (service, _, _, _) = CreateService(db);

        var result = await service.UpdateAsync(new UpdateAiProviderSettingsRequest(baseUrl, apiKey, false), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(await db.AiProviderConfigs.AnyAsync());
    }

    /// <summary>A circuit opened by a bad key must not keep blocking classification once the key is fixed.</summary>
    [Fact]
    public async Task UpdateAsync_ResetsOpenCircuitsForProvider()
    {
        using var db = TestDbContext.CreateNew();
        var (service, _, _, breaker) = CreateService(db);
        for (var i = 0; i < 3; i++)
        {
            breaker.TryAcquire("OpenRouter", "openai/gpt-4o-mini", out _);
            breaker.ReportOutcome("OpenRouter", "openai/gpt-4o-mini", CircuitOutcome.CountedFailure);
        }
        Assert.Contains(breaker.GetSnapshot(), s => s.State == CircuitState.Open);

        await service.UpdateAsync(new UpdateAiProviderSettingsRequest(null, "sk-or-v1-fixed-key-3333", false), CancellationToken.None);

        Assert.Empty(breaker.GetSnapshot());
    }

    [Fact]
    public async Task TestAsync_NoKey_DoesNotCallProvider()
    {
        using var db = TestDbContext.CreateNew();
        var (service, _, client, _) = CreateService(db);

        var result = await service.TestAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(client.LastChecked);
    }

    [Fact]
    public async Task AiModelService_UpdateAsync_CanChangeModelIdentifier()
    {
        using var db = TestDbContext.CreateNew();
        var model = new AiModelConfig
        {
            Provider = "OpenRouter", ModelIdentifier = "openai/gpt-4o-mini", DisplayName = "Default",
            Enabled = true, TaskCapability = "EmailClassification", TimeoutSeconds = 30, MaxRetries = 2,
        };
        db.AiModelConfigs.Add(model);
        db.AiModelConfigs.Add(new AiModelConfig { Provider = "OpenRouter", ModelIdentifier = "anthropic/claude-haiku", DisplayName = "Other", TaskCapability = "EmailClassification" });
        await db.SaveChangesAsync();
        var service = new AiModelService(db, new NoOpAuditService(), new FakeAiClassificationProvider());

        var duplicate = await service.UpdateAsync(model.Id,
            new UpdateAiModelRequest("Default", true, false, "EmailClassification", 30, 2, 0, "anthropic/claude-haiku"), CancellationToken.None);
        var changed = await service.UpdateAsync(model.Id,
            new UpdateAiModelRequest("Primary", true, false, "EmailClassification", 45, 1, 3, "google/gemini-flash"), CancellationToken.None);

        Assert.False(duplicate.Succeeded);
        Assert.True(changed.Succeeded, changed.Error);
        Assert.Equal("google/gemini-flash", changed.Value!.ModelIdentifier);
        Assert.Equal(45, changed.Value.TimeoutSeconds);
        Assert.Equal(3, changed.Value.FallbackOrder);
    }
}
