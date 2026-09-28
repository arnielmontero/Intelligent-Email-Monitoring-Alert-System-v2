using Iemas.Application.AiModels;

namespace Iemas.Tests.TestSupport;

public class StaticAiProviderConnectionResolver : IAiProviderConnectionResolver
{
    private readonly AiProviderConnection _connection;

    public StaticAiProviderConnectionResolver(string? apiKey, string baseUrl = AiProviderNames.OpenRouterDefaultBaseUrl, string? source = null)
    {
        _connection = new AiProviderConnection(baseUrl, string.IsNullOrEmpty(apiKey) ? null : apiKey, source ?? (string.IsNullOrEmpty(apiKey) ? "None" : "Environment"));
    }

    public Task<AiProviderConnection> ResolveAsync(CancellationToken cancellationToken) => Task.FromResult(_connection);
}
