namespace Iemas.Application.Auth.Dtos;

public record LoginResponse(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    Guid UserId,
    string Email,
    string DisplayName,
    IReadOnlyCollection<string> Roles);

public record RefreshTokenRequest(string RefreshToken);
