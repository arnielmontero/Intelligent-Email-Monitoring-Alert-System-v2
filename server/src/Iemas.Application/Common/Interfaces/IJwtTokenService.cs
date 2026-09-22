using Iemas.Domain.Identity;

namespace Iemas.Application.Common.Interfaces;

public interface IJwtTokenService
{
    string GenerateAccessToken(User user, IEnumerable<string> roles);
    (string Token, DateTimeOffset ExpiresAt) GenerateRefreshToken();
}
