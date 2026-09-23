using System.Text;
using System.Text.Json;
using Iemas.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iemas.Tests.Security;

public class GlobalExceptionHandlerTests
{
    [Fact]
    public async Task TryHandleAsync_ReturnsTrue_AndSets500StatusCode()
    {
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var handled = await handler.TryHandleAsync(context, new InvalidOperationException("boom"), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
    }

    [Fact]
    public async Task TryHandleAsync_ResponseBody_DoesNotContainExceptionMessageOrStackTrace()
    {
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        const string secretDetail = "Connection string password=SuperSecretDbPassword123 at line 42 of AesGcmCredentialEncryptionService.Decrypt";
        Exception exception;
        try
        {
            // Throw for real so the exception carries an actual stack trace, not just a message.
            throw new InvalidOperationException(secretDetail);
        }
        catch (InvalidOperationException ex)
        {
            exception = ex;
        }

        await handler.TryHandleAsync(context, exception, CancellationToken.None);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();

        Assert.DoesNotContain(secretDetail, body);
        Assert.DoesNotContain("SuperSecretDbPassword123", body);
        Assert.DoesNotContain(nameof(InvalidOperationException), body);
        Assert.DoesNotContain("GlobalExceptionHandlerTests", body); // no stack trace / type/file names
        Assert.DoesNotContain(".cs:line", body);
    }

    [Fact]
    public async Task TryHandleAsync_ResponseBody_IsGenericProblemDetailsJson()
    {
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/cases";
        context.Response.Body = new MemoryStream();

        await handler.TryHandleAsync(context, new Exception("anything"), CancellationToken.None);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Equal(500, root.GetProperty("status").GetInt32());
        Assert.Equal("An unexpected error occurred.", root.GetProperty("title").GetString());
        Assert.Equal("/api/v1/cases", root.GetProperty("instance").GetString());
    }
}
