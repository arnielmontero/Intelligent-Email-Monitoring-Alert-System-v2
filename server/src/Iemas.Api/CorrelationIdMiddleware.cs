using Serilog.Context;

namespace Iemas.Api;

// Observability (Phase 10 hardening): every HTTP request gets a correlation ID, visible in every
// structured log line for that request (via Serilog's LogContext, already enriched in Program.cs)
// and returned to the caller so a client (CMS, Windows Agent) can report it back when asking for
// help diagnosing a specific request. Accepts a caller-supplied ID (useful when the Windows Agent or
// CMS wants to correlate its own client-side logs with the server's), but only within a bounded
// length/character set — an unbounded or malformed caller-supplied value should never end up
// verbatim in every subsequent log line for this request.
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-ID";
    private const int MaxLength = 100;

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            context.Items["CorrelationId"] = correlationId;
            await _next(context);
        }
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out var values))
        {
            var candidate = values.ToString();
            if (!string.IsNullOrWhiteSpace(candidate)
                && candidate.Length <= MaxLength
                && candidate.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
            {
                return candidate;
            }
        }

        return Guid.NewGuid().ToString("n");
    }
}
