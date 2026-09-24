using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Iemas.Agent.Services;

/// <summary>
/// Requirements §8.2 ("must not store mailbox passwords / API keys" — this stores neither; it
/// stores only the server-issued Agent identity/credential per §69/§70), §10 "Secure local
/// credential storage". Uses Windows DPAPI (<see cref="ProtectedData"/>, CurrentUser scope) rather
/// than a self-generated encryption key — the OS user-profile master key is what protects this at
/// rest, matching "never a self-generated key" from the task's own build instruction. Never stores
/// a mailbox password, an OpenRouter/API key, or anything beyond this Agent's own server-issued
/// AgentId + Registration Key (§70: generated only by the server, never by the Agent).
/// </summary>
public class CredentialStore
{
    private static readonly string StoreDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IemasAgent");

    private static readonly string StorePath = Path.Combine(StoreDirectory, "agent.dat");

    private record StoredCredential(Guid AgentId, string RegistrationKey, string ServerUrl);

    public record AgentIdentity(Guid AgentId, string RegistrationKey, string ServerUrl);

    public bool HasStoredCredential() => File.Exists(StorePath);

    public AgentIdentity? Load()
    {
        if (!File.Exists(StorePath))
        {
            return null;
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(StorePath);
            // CurrentUser scope: only decryptable by the same Windows user account that saved it —
            // exactly the boundary DPAPI is designed for on a shared workstation.
            var plainBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(plainBytes);
            var stored = JsonSerializer.Deserialize<StoredCredential>(json);
            return stored is null ? null : new AgentIdentity(stored.AgentId, stored.RegistrationKey, stored.ServerUrl);
        }
        catch (CryptographicException)
        {
            // Corrupt, or profile/DPAPI key changed (e.g. Windows profile reset) — treat as "no
            // credential," which sends the Agent back through registration rather than crashing.
            return null;
        }
    }

    public void Save(Guid agentId, string registrationKey, string serverUrl)
    {
        Directory.CreateDirectory(StoreDirectory);
        var json = JsonSerializer.Serialize(new StoredCredential(agentId, registrationKey, serverUrl));
        var plainBytes = Encoding.UTF8.GetBytes(json);
        var protectedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(StorePath, protectedBytes);
    }

    public void Clear()
    {
        if (File.Exists(StorePath))
        {
            File.Delete(StorePath);
        }
    }
}
