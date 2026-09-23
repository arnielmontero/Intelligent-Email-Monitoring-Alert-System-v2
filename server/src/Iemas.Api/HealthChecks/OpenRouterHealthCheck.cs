using Iemas.Application.Common.Ai;
using Iemas.Infrastructure.Ai;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Iemas.Api.HealthChecks;

// Observability (Phase 10 hardening): reports OpenRouter health from configuration validity and
// the real, already-observed circuit-breaker state (AiCircuitBreakerStore, populated by actual
// classification attempts) rather than making a live inference call on every poll of this
// endpoint — that would consume real API quota/cost merely to answer "is the health endpoint
// green," which is exactly the kind of health-check-causes-production-work problem to avoid.
//
// Per the circuit breaker's own design (Iemas.Application.Common.Ai.AiCircuitBreakerStore, and the
// fallback architecture it sits on top of), one fallback model's circuit being OPEN does not mean
// classification as a whole is unable to function — so this check only reports Unhealthy if every
// model this process has ever attempted is currently OPEN, and Degraded if some (but not all) are.
public sealed class OpenRouterHealthCheck : IHealthCheck
{
    private readonly IOptions<AiClassificationOptions> _options;
    private readonly AiCircuitBreakerStore _circuitBreakerStore;

    public OpenRouterHealthCheck(IOptions<AiClassificationOptions> options, AiCircuitBreakerStore circuitBreakerStore)
    {
        _options = options;
        _circuitBreakerStore = circuitBreakerStore;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var options = _options.Value;

        if (!options.Enabled)
        {
            return Task.FromResult(HealthCheckResult.Healthy("AI classification is disabled by configuration; nothing to check."));
        }

        if (string.IsNullOrWhiteSpace(options.OpenRouter.ApiKey))
        {
            // Not Unhealthy: this is a known, already-tracked configuration gap in this
            // environment (see the tracker's "Irreducibly Not Verified" section), not a runtime
            // failure — classification cleanly reports ReviewRequired for every message rather than
            // losing anything, so the system is degraded, not down.
            return Task.FromResult(HealthCheckResult.Degraded("OpenRouter API key is not configured; classification will report every message as requiring manual review."));
        }

        if (string.IsNullOrWhiteSpace(options.OpenRouter.BaseUrl))
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("OpenRouter BaseUrl is not configured."));
        }

        var snapshot = _circuitBreakerStore.GetSnapshot();
        var data = new Dictionary<string, object>
        {
            ["modelsTracked"] = snapshot.Count,
            ["modelsOpen"] = snapshot.Count(s => s.State == CircuitState.Open),
            ["modelsHalfOpen"] = snapshot.Count(s => s.State == CircuitState.HalfOpen),
        };

        if (snapshot.Count == 0)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Configuration valid; no classification attempts observed yet this process.", data));
        }

        var openModels = snapshot.Where(s => s.State == CircuitState.Open).ToList();
        if (openModels.Count == snapshot.Count)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"All {snapshot.Count} tracked model(s) currently have an OPEN circuit: {string.Join(", ", openModels.Select(m => m.ModelIdentifier))}.",
                data: data));
        }

        if (openModels.Count > 0)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"{openModels.Count} of {snapshot.Count} tracked model(s) have an OPEN circuit, but at least one model remains available for fallback: {string.Join(", ", openModels.Select(m => m.ModelIdentifier))}.",
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy($"Configuration valid; all {snapshot.Count} tracked model(s) have a closed circuit.", data));
    }
}
