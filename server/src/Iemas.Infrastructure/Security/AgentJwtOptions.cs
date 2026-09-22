namespace Iemas.Infrastructure.Security;

/// <summary>
/// §69 — Agent identity must be entirely separate from CMS user identity. A distinct signing
/// secret/issuer/audience means an Agent token can never be replayed against CMS endpoints and
/// vice versa, even if someone mistakenly reused the same bearer scheme — the signature itself
/// would not validate under the other scheme's key.
/// </summary>
public class AgentJwtOptions
{
    public const string SectionName = "AgentJwt";

    public string Issuer { get; set; } = "Iemas.Agent";
    public string Audience { get; set; } = "IemasAgents";
    public string Secret { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>§70 — Registration Key lifetime. Deliberately long (default) since re-provisioning a Windows Agent is a manual, admin-involved process, unlike a CMS user's short session.</summary>
    public int RegistrationKeyDays { get; set; } = 365;
}
