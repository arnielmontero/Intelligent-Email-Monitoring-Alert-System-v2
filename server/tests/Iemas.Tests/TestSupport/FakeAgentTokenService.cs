using Iemas.Application.Common.Interfaces;
using Iemas.Domain.Agents;

namespace Iemas.Tests.TestSupport;

/// <summary>
/// Deterministic-enough fake for AgentRegistrationService/AgentAuthService tests — real
/// cryptographic randomness/JWT signing is covered separately (production uses
/// Iemas.Infrastructure.Security.AgentTokenService, a real JWT + RandomNumberGenerator
/// implementation); this fake only needs to produce distinct, inspectable values per call.
/// </summary>
public class FakeAgentTokenService : IAgentTokenService
{
    private int _keyCounter;

    public Func<Agent, string>? AccessTokenBehavior { get; set; }
    public TimeSpan RegistrationKeyLifetime { get; set; } = TimeSpan.FromDays(365);

    public string GenerateAccessToken(Agent agent)
    {
        return AccessTokenBehavior?.Invoke(agent) ?? $"fake-access-token-{agent.Id}";
    }

    public (string RawKey, DateTimeOffset ExpiresAt) GenerateRegistrationKey()
    {
        _keyCounter++;
        return ($"fake-registration-key-{_keyCounter}-{Guid.NewGuid():N}", DateTimeOffset.UtcNow.Add(RegistrationKeyLifetime));
    }
}
