using Iemas.Application.Common;
using Iemas.Application.Common.Ai;
using Iemas.Application.Common.Interfaces;
using Iemas.Domain.Ai;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.AiModels;

public static class AiProviderNames
{
    public const string OpenRouter = "OpenRouter";
    public const string OpenRouterDefaultBaseUrl = "https://openrouter.ai/api/v1";
}

/// <summary>The effective connection used for AI calls. KeySource is "CMS", "Environment" or "None".</summary>
public record AiProviderConnection(string BaseUrl, string? ApiKey, string KeySource);

public interface IAiProviderConnectionResolver
{
    Task<AiProviderConnection> ResolveAsync(CancellationToken cancellationToken);
}

public record AiProviderKeyCheck(bool Succeeded, string Message, long DurationMs, string? Label = null, decimal? UsageUsd = null, decimal? LimitUsd = null, decimal? LimitRemainingUsd = null);

public record AiCatalogModel(string Id, string Name, int? ContextLength, decimal? PromptPricePerMillion, decimal? CompletionPricePerMillion);

public interface IAiProviderAdminClient
{
    /// <summary>Validates the key without running a completion (no credit is spent).</summary>
    Task<AiProviderKeyCheck> CheckKeyAsync(AiProviderConnection connection, CancellationToken cancellationToken);

    Task<IReadOnlyList<AiCatalogModel>> GetModelsAsync(string baseUrl, CancellationToken cancellationToken);
}

public record AiProviderSettingsDto(
    string Provider,
    string BaseUrl,
    string DefaultBaseUrl,
    bool HasApiKey,
    string? ApiKeyHint,
    string KeySource,
    string? UpdatedByEmail,
    DateTimeOffset? UpdatedAt);

/// <summary>ApiKey null or empty keeps the current key; ClearApiKey removes the CMS key (falling back to server configuration).</summary>
public record UpdateAiProviderSettingsRequest(string? BaseUrl, string? ApiKey, bool ClearApiKey);

public class AiProviderSettingsService
{
    private readonly IAppDbContext _db;
    private readonly ICredentialEncryptionService _encryption;
    private readonly IAiProviderConnectionResolver _resolver;
    private readonly IAiProviderAdminClient _adminClient;
    private readonly AiCircuitBreakerStore _circuitBreaker;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditService _auditService;

    public AiProviderSettingsService(
        IAppDbContext db,
        ICredentialEncryptionService encryption,
        IAiProviderConnectionResolver resolver,
        IAiProviderAdminClient adminClient,
        AiCircuitBreakerStore circuitBreaker,
        ICurrentUserService currentUser,
        IAuditService auditService)
    {
        _db = db;
        _encryption = encryption;
        _resolver = resolver;
        _adminClient = adminClient;
        _circuitBreaker = circuitBreaker;
        _currentUser = currentUser;
        _auditService = auditService;
    }

    public async Task<AiProviderSettingsDto> GetAsync(CancellationToken cancellationToken)
    {
        var row = await _db.AiProviderConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.Provider == AiProviderNames.OpenRouter, cancellationToken);
        var connection = await _resolver.ResolveAsync(cancellationToken);
        var hint = connection.KeySource switch
        {
            "CMS" => row?.ApiKeyHint,
            "Environment" => Hint(connection.ApiKey!),
            _ => null,
        };
        return new AiProviderSettingsDto(
            AiProviderNames.OpenRouter, connection.BaseUrl, AiProviderNames.OpenRouterDefaultBaseUrl,
            connection.KeySource != "None", hint, connection.KeySource,
            row?.UpdatedByEmail, row?.UpdatedAt ?? row?.CreatedAt);
    }

    public async Task<Result<AiProviderSettingsDto>> UpdateAsync(UpdateAiProviderSettingsRequest request, CancellationToken cancellationToken)
    {
        var baseUrl = request.BaseUrl?.Trim().TrimEnd('/');
        if (!string.IsNullOrEmpty(baseUrl)
            && (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)))
        {
            return Result<AiProviderSettingsDto>.Failure("Base URL must be a full http(s) address, e.g. https://openrouter.ai/api/v1.");
        }

        var apiKey = request.ApiKey?.Trim();
        if (!string.IsNullOrEmpty(apiKey) && (apiKey.Length is < 10 or > 300 || apiKey.Any(char.IsWhiteSpace)))
        {
            return Result<AiProviderSettingsDto>.Failure("The API key does not look valid (10-300 characters, no spaces).");
        }

        var row = await _db.AiProviderConfigs.FirstOrDefaultAsync(c => c.Provider == AiProviderNames.OpenRouter, cancellationToken);
        if (row is null)
        {
            row = new AiProviderConfig { Provider = AiProviderNames.OpenRouter };
            _db.AiProviderConfigs.Add(row);
        }

        var changes = new List<string>();
        var newBaseUrl = string.IsNullOrEmpty(baseUrl) ? null : baseUrl;
        if (row.BaseUrl != newBaseUrl)
        {
            changes.Add($"base URL: '{row.BaseUrl ?? "(server default)"}' -> '{newBaseUrl ?? "(server default)"}'");
            row.BaseUrl = newBaseUrl;
        }

        if (request.ClearApiKey && row.EncryptedApiKey is not null)
        {
            row.EncryptedApiKey = row.ApiKeyNonce = row.ApiKeyTag = null;
            row.ApiKeyEncryptionKeyId = row.ApiKeyHint = null;
            changes.Add("API key removed");
        }
        else if (!string.IsNullOrEmpty(apiKey))
        {
            var encrypted = _encryption.Encrypt(apiKey);
            row.EncryptedApiKey = encrypted.Ciphertext;
            row.ApiKeyNonce = encrypted.Nonce;
            row.ApiKeyTag = encrypted.Tag;
            row.ApiKeyEncryptionKeyId = encrypted.KeyId;
            row.ApiKeyHint = Hint(apiKey);
            changes.Add($"API key set (ending {row.ApiKeyHint})");
        }

        if (changes.Count > 0)
        {
            row.UpdatedByUserId = _currentUser.UserId;
            row.UpdatedByEmail = _currentUser.Email;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);

            // Circuits opened by a bad or missing key should not keep blocking calls once it is fixed.
            _circuitBreaker.ResetProvider(AiProviderNames.OpenRouter);
            await _auditService.LogAsync("AI_PROVIDER_SETTINGS_UPDATED", "AiProviderConfig", AiProviderNames.OpenRouter,
                string.Join("; ", changes), cancellationToken);
        }

        return Result<AiProviderSettingsDto>.Success(await GetAsync(cancellationToken));
    }

    public async Task<AiProviderKeyCheck> TestAsync(CancellationToken cancellationToken)
    {
        var connection = await _resolver.ResolveAsync(cancellationToken);
        if (string.IsNullOrEmpty(connection.ApiKey))
        {
            return new AiProviderKeyCheck(false, "No API key is configured.", 0);
        }
        return await _adminClient.CheckKeyAsync(connection, cancellationToken);
    }

    public async Task<IReadOnlyList<AiCatalogModel>> GetCatalogAsync(CancellationToken cancellationToken)
    {
        var connection = await _resolver.ResolveAsync(cancellationToken);
        return await _adminClient.GetModelsAsync(connection.BaseUrl, cancellationToken);
    }

    private static string Hint(string apiKey) => apiKey.Length <= 4 ? "****" : apiKey[^4..];
}
