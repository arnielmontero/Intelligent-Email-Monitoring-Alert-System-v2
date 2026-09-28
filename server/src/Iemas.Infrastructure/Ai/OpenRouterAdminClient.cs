using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Iemas.Application.AiModels;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Iemas.Infrastructure.Ai;

/// <summary>
/// OpenRouter account-level calls for the CMS: key validation via GET /auth/key (no completion,
/// so no credit is spent) and the public model catalog via GET /models, cached for 10 minutes.
/// </summary>
public class OpenRouterAdminClient : IAiProviderAdminClient
{
    private static readonly TimeSpan CatalogCacheDuration = TimeSpan.FromMinutes(10);

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly ILogger<OpenRouterAdminClient> _logger;

    public OpenRouterAdminClient(HttpClient httpClient, IMemoryCache cache, ILogger<OpenRouterAdminClient> logger)
    {
        _httpClient = httpClient;
        _cache = cache;
        _logger = logger;
    }

    public async Task<AiProviderKeyCheck> CheckKeyAsync(AiProviderConnection connection, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{connection.BaseUrl}/auth/key");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiKey);
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new AiProviderKeyCheck(false, "OpenRouter rejected the API key (invalid or revoked).", stopwatch.ElapsedMilliseconds);
            }
            if (!response.IsSuccessStatusCode)
            {
                return new AiProviderKeyCheck(false, $"OpenRouter returned HTTP {(int)response.StatusCode}.", stopwatch.ElapsedMilliseconds);
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var data = doc.RootElement.TryGetProperty("data", out var d) ? d : doc.RootElement;
            var parts = new List<string> { "API key is valid" };
            var labelText = data.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String ? label.GetString() : null;
            if (!string.IsNullOrWhiteSpace(labelText))
            {
                parts.Add($"label \"{labelText}\"");
            }
            // OpenRouter sends null for "limit"/"limit_remaining" on keys without a spending limit.
            decimal? used = ReadNumber(data, "usage");
            decimal? max = ReadNumber(data, "limit");
            decimal? remaining = ReadNumber(data, "limit_remaining");
            if (used is not null) parts.Add($"used ${used.Value.ToString("0.####", CultureInfo.InvariantCulture)}");
            if (max is not null) parts.Add($"limit ${max.Value.ToString("0.##", CultureInfo.InvariantCulture)}");
            return new AiProviderKeyCheck(true, string.Join(", ", parts) + ".", stopwatch.ElapsedMilliseconds, labelText, used, max, remaining);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "OpenRouter key check failed");
            return new AiProviderKeyCheck(false, $"Could not reach OpenRouter at {connection.BaseUrl}: {ex.Message}", stopwatch.ElapsedMilliseconds);
        }
    }

    public async Task<IReadOnlyList<AiCatalogModel>> GetModelsAsync(string baseUrl, CancellationToken cancellationToken)
    {
        var cacheKey = $"openrouter-models:{baseUrl}";
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<AiCatalogModel>? cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            using var response = await _httpClient.GetAsync($"{baseUrl}/models", cancellationToken);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

            var models = new List<AiCatalogModel>();
            foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) continue;
                var name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? id : id;
                int? context = item.TryGetProperty("context_length", out var ctxEl) && ctxEl.TryGetInt32(out var c) ? c : null;
                decimal? prompt = null, completion = null;
                if (item.TryGetProperty("pricing", out var pricing))
                {
                    prompt = PerMillion(pricing, "prompt");
                    completion = PerMillion(pricing, "completion");
                }
                models.Add(new AiCatalogModel(id, name, context, prompt, completion));
            }

            var sorted = models.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
            _cache.Set(cacheKey, (IReadOnlyList<AiCatalogModel>)sorted, CatalogCacheDuration);
            return sorted;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not load the OpenRouter model catalog from {BaseUrl}", baseUrl);
            return Array.Empty<AiCatalogModel>();
        }
    }

    private static decimal? ReadNumber(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
            ? number
            : null;

    /// <summary>OpenRouter prices are USD per token, as strings; shown per million tokens.</summary>
    private static decimal? PerMillion(JsonElement pricing, string property) =>
        pricing.TryGetProperty(property, out var el)
        && decimal.TryParse(el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var perToken)
            ? Math.Round(perToken * 1_000_000m, 4)
            : null;
}
