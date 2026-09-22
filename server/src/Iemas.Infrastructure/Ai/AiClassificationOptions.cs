namespace Iemas.Infrastructure.Ai;

/// <summary>
/// Requirements §82 — non-secret AI configuration (base URL, default timeout) lives in
/// appsettings; the API key is supplied only via environment/secret configuration (§16 — "AI
/// provider keys" must never be hardcoded or exposed), matching the
/// <see cref="Iemas.Infrastructure.Security.CredentialEncryptionOptions"/> precedent.
/// </summary>
public class AiClassificationOptions
{
    public const string SectionName = "AiClassification";

    public bool Enabled { get; set; } = true;
    public string CronSchedule { get; set; } = "*/2 * * * *";
    public int BatchSize { get; set; } = 25;

    public OpenRouterOptions OpenRouter { get; set; } = new();
}

public class OpenRouterOptions
{
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";

    /// <summary>Never logged, never returned by any API response (§16).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>OpenRouter uses this for its own analytics/rate-limit attribution; not secret.</summary>
    public string? SiteUrl { get; set; }
    public string? SiteName { get; set; }
}
