using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Iemas.Application.Common.Interfaces;
using Iemas.Domain.Agents;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Iemas.Infrastructure.Security;

/// <summary>§69/§70/§71 — see IAgentTokenService and AgentJwtOptions for the identity-separation rationale.</summary>
public class AgentTokenService : IAgentTokenService
{
    private readonly AgentJwtOptions _options;

    public AgentTokenService(IOptions<AgentJwtOptions> options)
    {
        _options = options.Value;
    }

    public string GenerateAccessToken(Agent agent)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, agent.Id.ToString()),
            new("agent_id", agent.Id.ToString()),
            new("employee_id", agent.EmployeeId?.ToString() ?? string.Empty),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_options.AccessTokenMinutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public (string RawKey, DateTimeOffset ExpiresAt) GenerateRegistrationKey()
    {
        // §70 — cryptographically random, server-generated only, never derived from anything the
        // Agent supplies (email/client name/IP are never inputs to this).
        var bytes = RandomNumberGenerator.GetBytes(48);
        var rawKey = Convert.ToBase64String(bytes);
        var expiresAt = DateTimeOffset.UtcNow.AddDays(_options.RegistrationKeyDays);
        return (rawKey, expiresAt);
    }
}
