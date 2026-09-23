using Iemas.Api.HealthChecks;
using Iemas.Application.Common.Ai;
using Iemas.Infrastructure.Ai;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Iemas.Tests.HealthChecks;

public class OpenRouterHealthCheckTests
{
    private static OpenRouterHealthCheck CreateCheck(AiClassificationOptions options, AiCircuitBreakerStore? store = null) =>
        new(Options.Create(options), store ?? new AiCircuitBreakerStore(TimeProvider.System));

    [Fact]
    public async Task CheckHealthAsync_ClassificationDisabled_ReturnsHealthy_WithoutInspectingApiKey()
    {
        var check = CreateCheck(new AiClassificationOptions { Enabled = false, OpenRouter = new OpenRouterOptions { ApiKey = "" } });

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_MissingApiKey_ReturnsDegraded_NotUnhealthy()
    {
        // A missing key is a known, already-tracked configuration gap (no real OpenRouter key
        // available in this environment) that fails cleanly (every message -> ReviewRequired), not
        // a runtime outage — Degraded, not Unhealthy, matches that severity.
        var check = CreateCheck(new AiClassificationOptions { Enabled = true, OpenRouter = new OpenRouterOptions { ApiKey = "", BaseUrl = "https://openrouter.ai/api/v1" } });

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_MissingBaseUrl_ReturnsUnhealthy()
    {
        var check = CreateCheck(new AiClassificationOptions { Enabled = true, OpenRouter = new OpenRouterOptions { ApiKey = "sk-real-key", BaseUrl = "" } });

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_ValidConfig_NoCircuitActivityYet_ReturnsHealthy()
    {
        var check = CreateCheck(new AiClassificationOptions { Enabled = true, OpenRouter = new OpenRouterOptions { ApiKey = "sk-real-key", BaseUrl = "https://openrouter.ai/api/v1" } });

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_SomeModelsOpenButNotAll_ReturnsDegraded_NotUnhealthy()
    {
        // Reflects the fallback architecture: one model's circuit being OPEN does not mean
        // classification as a whole is unable to function, since a fallback model remains.
        var store = new AiCircuitBreakerStore(TimeProvider.System);
        OpenCircuit(store, "OpenRouter", "primary-model");
        var check = CreateCheck(
            new AiClassificationOptions { Enabled = true, OpenRouter = new OpenRouterOptions { ApiKey = "sk-real-key", BaseUrl = "https://openrouter.ai/api/v1" } },
            store);
        // Record one success for a second model so the store has more than one tracked entry.
        store.TryAcquire("OpenRouter", "fallback-model", out _);
        store.ReportOutcome("OpenRouter", "fallback-model", CircuitOutcome.Success);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_EveryTrackedModelOpen_ReturnsUnhealthy()
    {
        var store = new AiCircuitBreakerStore(TimeProvider.System);
        OpenCircuit(store, "OpenRouter", "only-model");
        var check = CreateCheck(
            new AiClassificationOptions { Enabled = true, OpenRouter = new OpenRouterOptions { ApiKey = "sk-real-key", BaseUrl = "https://openrouter.ai/api/v1" } },
            store);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    private static void OpenCircuit(AiCircuitBreakerStore store, string provider, string model)
    {
        for (var i = 0; i < 3; i++)
        {
            store.TryAcquire(provider, model, out _);
            store.ReportOutcome(provider, model, CircuitOutcome.CountedFailure);
        }
    }
}
