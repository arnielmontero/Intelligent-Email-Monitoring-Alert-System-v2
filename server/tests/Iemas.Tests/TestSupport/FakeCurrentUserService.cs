using Iemas.Application.Common.Interfaces;

namespace Iemas.Tests.TestSupport;

public class FakeCurrentUserService : ICurrentUserService
{
    public FakeCurrentUserService(Guid? userId = null, string? email = "admin@sawo.com", params string[] roles)
    {
        UserId = userId ?? Guid.NewGuid();
        Email = email;
        Roles = roles;
    }

    public Guid? UserId { get; }
    public string? Email { get; }
    public IReadOnlyCollection<string> Roles { get; }
    public string? IpAddress => "127.0.0.1";
}

/// <summary>Deterministic, fast stand-in for BCrypt so password tests don't pay the work-factor cost.</summary>
public class PlainPasswordHasher : IPasswordHasher
{
    public string Hash(string password) => "hashed:" + password;
    public bool Verify(string password, string hash) => hash == "hashed:" + password;
}
