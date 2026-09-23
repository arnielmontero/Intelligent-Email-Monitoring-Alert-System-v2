using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Iemas.Api.HealthChecks;

// The default ASP.NET Core health check response is a bare "Healthy"/"Unhealthy" string — not
// useful for distinguishing *which* dependency is degraded/unhealthy without going to the logs.
// This renders each individual check's name, status, description, duration, and any diagnostic
// data (e.g. IMAP's per-account failure counts) as JSON, while staying within the same
// no-secrets-in-telemetry discipline the rest of this session's logging follows — each check's own
// `data` dictionary is built by that check, so a check must not put anything secret in there
// (verified by inspection: none of the four checks include credentials/keys/tokens).
public static class HealthCheckJsonWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                durationMs = e.Value.Duration.TotalMilliseconds,
                data = e.Value.Data.Count > 0 ? e.Value.Data : null,
            }),
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload, SerializerOptions));
    }
}
