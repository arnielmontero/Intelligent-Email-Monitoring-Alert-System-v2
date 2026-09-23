using Iemas.Api;
using Microsoft.AspNetCore.Http;

namespace Iemas.Tests.Security;

/// <summary>
/// These tests assert against <c>HttpContext.Items["CorrelationId"]</c> rather than the
/// X-Correlation-ID response header: the middleware sets the header inside an
/// <c>HttpResponse.OnStarting</c> callback (the correct, real-pipeline way to set a header after
/// downstream middleware may have already started writing to the response body), but
/// <c>DefaultHttpContext</c>'s in-memory response in a unit test does not invoke OnStarting
/// callbacks the way a real Kestrel response does — only a full <c>WebApplicationFactory</c>
/// integration test would exercise that path. <c>Items["CorrelationId"]</c> is set synchronously
/// and unconditionally by the middleware and holds the exact same value the header would carry, so
/// it is what downstream code (e.g. GlobalExceptionHandler) actually reads.
/// </summary>
public class CorrelationIdMiddlewareTests
{
    private static (CorrelationIdMiddleware Middleware, DefaultHttpContext Context) CreateSut(RequestDelegate? next = null)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var middleware = new CorrelationIdMiddleware(next ?? (_ => Task.CompletedTask));
        return (middleware, context);
    }

    [Fact]
    public async Task InvokeAsync_NoCallerHeader_GeneratesANonEmptyCorrelationId()
    {
        var (middleware, context) = CreateSut();

        await middleware.InvokeAsync(context);

        var value = Assert.IsType<string>(context.Items["CorrelationId"]);
        Assert.False(string.IsNullOrWhiteSpace(value));
    }

    [Fact]
    public async Task InvokeAsync_ValidCallerSuppliedHeader_IsEchoedBackUnchanged()
    {
        var (middleware, context) = CreateSut();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = "agent-run-12345";

        await middleware.InvokeAsync(context);

        Assert.Equal("agent-run-12345", context.Items["CorrelationId"]);
    }

    [Theory]
    [InlineData("has spaces")]
    [InlineData("has;semicolon")]
    [InlineData("has\nnewline")]
    [InlineData("has<script>")]
    public async Task InvokeAsync_MalformedCallerSuppliedHeader_IsRejectedAndReplacedWithAGeneratedId(string malformed)
    {
        var (middleware, context) = CreateSut();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = malformed;

        await middleware.InvokeAsync(context);

        var value = Assert.IsType<string>(context.Items["CorrelationId"]);
        Assert.NotEqual(malformed, value);
        Assert.DoesNotContain(' ', value);
        Assert.DoesNotContain(';', value);
        Assert.DoesNotContain('\n', value);
        Assert.DoesNotContain('<', value);
    }

    [Fact]
    public async Task InvokeAsync_OverlongCallerSuppliedHeader_IsRejectedAndReplacedWithAGeneratedId()
    {
        var (middleware, context) = CreateSut();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = new string('a', 500);

        await middleware.InvokeAsync(context);

        var value = Assert.IsType<string>(context.Items["CorrelationId"]);
        Assert.True(value.Length < 500);
    }

    [Fact]
    public async Task InvokeAsync_StoresCorrelationIdOnHttpContextItems_BeforeCallingNext_ForDownstreamComponentsLikeGlobalExceptionHandler()
    {
        string? capturedFromItems = null;
        var (middleware, context) = CreateSut(next: ctx =>
        {
            capturedFromItems = ctx.Items["CorrelationId"] as string;
            return Task.CompletedTask;
        });
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = "known-id-abc";

        await middleware.InvokeAsync(context);

        Assert.Equal("known-id-abc", capturedFromItems);
    }

    [Fact]
    public async Task InvokeAsync_TwoSeparateRequestsWithNoCallerHeader_GetDifferentGeneratedIds()
    {
        var (middleware1, context1) = CreateSut();
        var (middleware2, context2) = CreateSut();

        await middleware1.InvokeAsync(context1);
        await middleware2.InvokeAsync(context2);

        Assert.NotEqual(context1.Items["CorrelationId"], context2.Items["CorrelationId"]);
    }

    [Fact]
    public async Task InvokeAsync_RegistersAnOnStartingCallback_ThatSetsTheResponseHeader()
    {
        // Directly exercises the OnStarting callback the middleware registers, since
        // DefaultHttpContext's response doesn't invoke it on its own in a unit test — this proves
        // the callback itself does the right thing without needing a full Kestrel pipeline.
        var (middleware, context) = CreateSut();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = "explicit-id-999";

        var onStartingCallbacks = new List<(Func<object, Task> Callback, object State)>();
        var feature = new RecordingHttpResponseFeature(onStartingCallbacks);
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(feature);

        await middleware.InvokeAsync(context);

        Assert.Single(onStartingCallbacks);
        foreach (var (callback, state) in onStartingCallbacks)
        {
            await callback(state);
        }

        Assert.Equal("explicit-id-999", context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString());
    }

    private sealed class RecordingHttpResponseFeature : Microsoft.AspNetCore.Http.Features.IHttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _callbacks;

        public RecordingHttpResponseFeature(List<(Func<object, Task> Callback, object State)> callbacks)
        {
            _callbacks = callbacks;
        }

        public int StatusCode { get; set; } = 200;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = new MemoryStream();
        public bool HasStarted => false;

        public void OnStarting(Func<object, Task> callback, object state) => _callbacks.Add((callback, state));
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }
}
