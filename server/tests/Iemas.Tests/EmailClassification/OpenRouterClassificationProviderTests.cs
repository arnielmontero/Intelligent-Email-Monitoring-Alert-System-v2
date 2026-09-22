using System.Net;
using System.Text;
using Iemas.Application.Common.Ai;
using Iemas.Infrastructure.Ai;
using Iemas.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iemas.Tests.EmailClassification;

/// <summary>
/// §83 — provider failures, malformed/unexpected AI responses, and timeouts must never throw;
/// every case is reported as a failed ClassificationAttemptResult. Uses a fake HttpMessageHandler
/// so this exercises the provider's own parsing/error-handling logic without a real network call
/// or a real OpenRouter API key.
/// </summary>
public class OpenRouterClassificationProviderTests
{
    private static readonly ClassificationRequest SampleRequest = new(
        "Request for ABC Product Pricing", "Customer wants 50 units and requests latest price and availability.",
        "customer@example.com", "sales@sawo.com", false, "Sales", "PRODUCT_INQUIRY", "price", "newsletter");

    private static OpenRouterClassificationProvider CreateProvider(FakeHttpMessageHandler handler, string apiKey = "sk-test-key")
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://openrouter.ai/api/v1/") };
        var options = Options.Create(new AiClassificationOptions
        {
            OpenRouter = new OpenRouterOptions { BaseUrl = "https://openrouter.ai/api/v1", ApiKey = apiKey }
        });
        return new OpenRouterClassificationProvider(httpClient, options, NullLogger<OpenRouterClassificationProvider>.Instance);
    }

    [Fact]
    public async Task ClassifyAsync_MissingApiKey_FailsCleanlyWithoutCallingHttp()
    {
        var handler = new FakeHttpMessageHandler
        {
            Behavior = (_, _) => throw new InvalidOperationException("Should never reach HTTP when the API key is missing.")
        };
        var provider = CreateProvider(handler, apiKey: "");

        var result = await provider.ClassifyAsync(SampleRequest, "openai/gpt-4o-mini", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("API key is not configured", result.ErrorMessage);
    }

    [Fact]
    public async Task ClassifyAsync_ValidResponse_ParsesClassificationCorrectly()
    {
        var responseJson = """
            {"choices":[{"message":{"content":"{\"relevant\":true,\"category\":\"PRODUCT_INQUIRY\",\"action_required\":true,\"response_expected\":true,\"priority\":\"HIGH\",\"confidence\":0.95,\"summary\":\"Pricing request.\"}"}}]}
            """;
        var handler = new FakeHttpMessageHandler
        {
            Behavior = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            })
        };
        var provider = CreateProvider(handler);

        var result = await provider.ClassifyAsync(SampleRequest, "openai/gpt-4o-mini", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Response);
        Assert.True(result.Response!.Relevant);
        Assert.Equal("PRODUCT_INQUIRY", result.Response.Category);
        Assert.Equal(0.95, result.Response.Confidence);
    }

    [Fact]
    public async Task ClassifyAsync_NonSuccessHttpStatus_FailsCleanlyWithStatusInError()
    {
        var handler = new FakeHttpMessageHandler
        {
            Behavior = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("upstream overloaded")
            })
        };
        var provider = CreateProvider(handler);

        var result = await provider.ClassifyAsync(SampleRequest, "openai/gpt-4o-mini", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("503", result.ErrorMessage);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task ClassifyAsync_MalformedJsonBody_FailsCleanlyWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler
        {
            Behavior = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("this is not json at all {{{", Encoding.UTF8, "application/json")
            })
        };
        var provider = CreateProvider(handler);

        var result = await provider.ClassifyAsync(SampleRequest, "openai/gpt-4o-mini", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task ClassifyAsync_ModelContentIsNotValidClassificationJson_FailsCleanlyWithParseError()
    {
        // The chat completion envelope is valid JSON, but the model's own "content" text inside
        // it is not a valid classification object — e.g. the model ignored instructions.
        var responseJson = """
            {"choices":[{"message":{"content":"Sure, I can help you with that!"}}]}
            """;
        var handler = new FakeHttpMessageHandler
        {
            Behavior = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            })
        };
        var provider = CreateProvider(handler);

        var result = await provider.ClassifyAsync(SampleRequest, "openai/gpt-4o-mini", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Could not parse", result.ErrorMessage);
    }

    [Fact]
    public async Task ClassifyAsync_ConfidenceOutsideValidRange_IsRejectedAsInvalid()
    {
        var responseJson = """
            {"choices":[{"message":{"content":"{\"relevant\":true,\"category\":\"PRODUCT_INQUIRY\",\"action_required\":true,\"response_expected\":true,\"priority\":\"HIGH\",\"confidence\":1.5,\"summary\":\"x\"}"}}]}
            """;
        var handler = new FakeHttpMessageHandler
        {
            Behavior = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            })
        };
        var provider = CreateProvider(handler);

        var result = await provider.ClassifyAsync(SampleRequest, "openai/gpt-4o-mini", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("outside the valid 0-1 range", result.ErrorMessage);
    }

    [Fact]
    public async Task ClassifyAsync_InvalidPriorityValue_IsRejectedAsInvalid()
    {
        var responseJson = """
            {"choices":[{"message":{"content":"{\"relevant\":true,\"category\":\"PRODUCT_INQUIRY\",\"action_required\":true,\"response_expected\":true,\"priority\":\"URGENT\",\"confidence\":0.9,\"summary\":\"x\"}"}}]}
            """;
        var handler = new FakeHttpMessageHandler
        {
            Behavior = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            })
        };
        var provider = CreateProvider(handler);

        var result = await provider.ClassifyAsync(SampleRequest, "openai/gpt-4o-mini", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("URGENT", result.ErrorMessage);
    }

    [Fact]
    public async Task ClassifyAsync_RequestExceedsTimeout_FailsCleanlyAsTimeout_WithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler
        {
            Behavior = async (_, ct) =>
            {
                // Simulate a slow upstream; the provider's own CancelAfter(timeout) must fire
                // before this completes, and must be translated into a clean failure result.
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };
        var provider = CreateProvider(handler);

        var result = await provider.ClassifyAsync(SampleRequest, "openai/gpt-4o-mini", TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("timed out", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClassifyAsync_EmptyChoicesArray_FailsCleanlyAsEmptyResponse()
    {
        var handler = new FakeHttpMessageHandler
        {
            Behavior = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[]}""", Encoding.UTF8, "application/json")
            })
        };
        var provider = CreateProvider(handler);

        var result = await provider.ClassifyAsync(SampleRequest, "openai/gpt-4o-mini", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("empty response", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClassifyAsync_CallerCancellation_PropagatesAsCancellation_NotAsFailureResult()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeHttpMessageHandler
        {
            Behavior = async (_, ct) =>
            {
                cts.Cancel();
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };
        var provider = CreateProvider(handler);

        // TaskCanceledException derives from OperationCanceledException — HttpClient throws the
        // former, and either is an acceptable "this really was cancellation" signal here.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.ClassifyAsync(SampleRequest, "openai/gpt-4o-mini", TimeSpan.FromSeconds(30), cts.Token));
    }
}
