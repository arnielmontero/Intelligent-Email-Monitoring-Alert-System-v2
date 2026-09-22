using Iemas.Domain.Common;

namespace Iemas.Domain.Identity;

/// <summary>Requirements §84 — credential rotation and revocation.</summary>
public class RefreshToken : Entity
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsActive => RevokedAt is null && ExpiresAt > DateTimeOffset.UtcNow;
}
