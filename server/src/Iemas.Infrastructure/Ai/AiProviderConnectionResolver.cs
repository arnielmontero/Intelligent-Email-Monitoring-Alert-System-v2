using Iemas.Application.AiModels;
using Iemas.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Iemas.Infrastructure.Ai;

/// <summary>
/// The CMS-saved OpenRouter settings win; anything not saved there falls back to server
/// configuration (AiClassification:OpenRouter / OPENROUTER_API_KEY). Read per call so a key saved
/// in the CMS takes effect without a restart.
/// </summary>
public class AiProviderConnectionResolver : IAiProviderConnectionResolver
{
    private readonly IAppDbContext _db;
    private readonly ICredentialEncryptionService _encryption;
    private readonly OpenRouterOptions _options;

    public AiProviderConnectionResolver(IAppDbContext db, ICredentialEncryptionService encryption, IOptions<AiClassificationOptions> options)
    {
        _db = db;
        _encryption = encryption;
        _options = options.Value.OpenRouter;
    }

    public async Task<AiProviderConnection> ResolveAsync(CancellationToken cancellationToken)
    {
        var row = await _db.AiProviderConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Provider == AiProviderNames.OpenRouter, cancellationToken);

        var baseUrl = !string.IsNullOrWhiteSpace(row?.BaseUrl) ? row.BaseUrl
            : !string.IsNullOrWhiteSpace(_options.BaseUrl) ? _options.BaseUrl
            : AiProviderNames.OpenRouterDefaultBaseUrl;
        baseUrl = baseUrl.TrimEnd('/');

        if (row?.EncryptedApiKey is { Length: > 0 })
        {
            var key = _encryption.Decrypt(new EncryptedSecret(
                row.EncryptedApiKey, row.ApiKeyNonce ?? Array.Empty<byte>(), row.ApiKeyTag ?? Array.Empty<byte>(), row.ApiKeyEncryptionKeyId ?? "v1"));
            return new AiProviderConnection(baseUrl, key, "CMS");
        }

        return string.IsNullOrWhiteSpace(_options.ApiKey)
            ? new AiProviderConnection(baseUrl, null, "None")
            : new AiProviderConnection(baseUrl, _options.ApiKey, "Environment");
    }
}
