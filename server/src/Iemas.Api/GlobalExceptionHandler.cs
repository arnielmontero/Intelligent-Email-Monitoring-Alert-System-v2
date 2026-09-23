using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Iemas.Api;

// Last-resort handler for exceptions that escape controller action code (e.g. thrown from
// middleware, model binding, or a bug in a service that isn't using the Result<T> pattern).
// Never returns exception details/stack traces to the client — those go to the server log only.
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        _logger.LogError(
            exception,
            "Unhandled exception processing {Method} {Path}",
            httpContext.Request.Method,
            httpContext.Request.Path);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "An unexpected error occurred.",
            Detail = "The request could not be completed. Please try again or contact support if the problem persists.",
            Instance = httpContext.Request.Path
        };

        // Included so a caller can hand this back when reporting the issue — it is not sensitive
        // (it identifies the request, not the failure) and is already returned in the
        // X-Correlation-ID response header by CorrelationIdMiddleware regardless of outcome.
        if (httpContext.Items.TryGetValue("CorrelationId", out var correlationId) && correlationId is string id)
        {
            problemDetails.Extensions["correlationId"] = id;
        }

        await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);

        return true;
    }
}
