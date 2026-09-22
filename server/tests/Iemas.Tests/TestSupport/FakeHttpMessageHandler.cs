namespace Iemas.Tests.TestSupport;

/// <summary>Minimal fake HTTP transport so OpenRouterClassificationProvider tests never make real network calls.</summary>
public class FakeHttpMessageHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Behavior { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Behavior is null)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }

        return Behavior(request, cancellationToken);
    }
}
