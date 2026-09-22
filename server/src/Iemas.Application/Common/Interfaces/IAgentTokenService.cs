using Iemas.Domain.Agents;

namespace Iemas.Application.Common.Interfaces;

/// <summary>
/// §69 — Agent identity is a server-issued Agent ID + credential, entirely separate from CMS
/// <see cref="Iemas.Domain.Identity.User"/> login. Mirrors <see cref="IJwtTokenService"/>'s shape
/// but issues tokens on a distinct signing audience/scheme ("AgentScheme") so an Agent token can
/// never be used against CMS user-only endpoints and vice versa — see Infrastructure/DI wiring.
/// </summary>
public interface IAgentTokenService
{
    /// <summary>Short-lived bearer token proving this specific Agent authenticated successfully (§68 "Agent Authenticates" → CONNECTED).</summary>
    string GenerateAccessToken(Agent agent);

    /// <summary>§70 — the raw Registration Key. Generated only here, only server-side; returned to the caller once and never reconstructable from the stored hash.</summary>
    (string RawKey, DateTimeOffset ExpiresAt) GenerateRegistrationKey();
}
