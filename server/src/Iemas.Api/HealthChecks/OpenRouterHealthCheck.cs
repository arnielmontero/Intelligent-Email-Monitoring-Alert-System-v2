using Iemas.Application.AiModels;
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
    private readonly IAiProviderConnectionResolver _connectionResolver;
    private readonly AiCircuitBreakerStore _circuitBreakerStore;

    public OpenRouterHealthCheck(IOptions<AiClassificationOptions> options, IAiProviderConnectionResolver connectionResolver, AiCircuitBreakerStore circuitBreakerStore)
    {
        _options = options;
        _connectionResolver = connectionResolver;
        _circuitBreakerStore = circuitBreakerStore;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!_options.Value.Enabled)
        {
            return HealthCheckResult.Healthy("AI classification is disabled by configuration; nothing to check.");
        }

        var connection = await _connectionResolver.ResolveAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(connection.ApiKey))
        {
            // Not Unhealthy: this is a known, already-tracked configuration gap in this
            // environment (see the tracker's "Irreducibly Not Verified" section), not a runtime
            // failure — classification cleanly reports ReviewRequired for every message rather than
            // losing anything, so the system is degraded, not down.
            return HealthCheckResult.Degraded("OpenRouter API key is not configured (set it on the AI Models page); classification will report every message as requiring manual review.");
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
            return HealthCheckResult.Healthy("Configuration valid; no classification attempts observed yet this process.", data);
        }

        var openModels = snapshot.Where(s => s.State == CircuitState.Open).ToList();
        if (openModels.Count == snapshot.Count)
        {
            return HealthCheckResult.Unhealthy(
                $"All {snapshot.Count} tracked model(s) currently have an OPEN circuit: {string.Join(", ", openModels.Select(m => m.ModelIdentifier))}.",
                data: data);
        }

        if (openModels.Count > 0)
        {
            return HealthCheckResult.Degraded(
                $"{openModels.Count} of {snapshot.Count} tracked model(s) have an OPEN circuit, but at least one model remains available for fallback: {string.Join(", ", openModels.Select(m => m.ModelIdentifier))}.",
                data: data);
        }

        return HealthCheckResult.Healthy($"Configuration valid; all {snapshot.Count} tracked model(s) have a closed circuit.", data);
    }
}
