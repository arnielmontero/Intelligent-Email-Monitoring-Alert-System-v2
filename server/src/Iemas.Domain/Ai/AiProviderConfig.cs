using Iemas.Domain.Common;

namespace Iemas.Domain.Ai;

/// <summary>
/// Requirements §82/§84 — CMS-managed connection settings for an AI provider. The API key is
/// stored encrypted (same scheme as mailbox credentials, §16) and is write-only: only its last
/// four characters are ever shown back. Absent values fall back to server configuration.
/// </summary>
public class AiProviderConfig : Entity
{
    public string Provider { get; set; } = string.Empty;
    public string? BaseUrl { get; set; }

    public byte[]? EncryptedApiKey { get; set; }
    public byte[]? ApiKeyNonce { get; set; }
    public byte[]? ApiKeyTag { get; set; }
    public string? ApiKeyEncryptionKeyId { get; set; }
    public string? ApiKeyHint { get; set; }

    public Guid? UpdatedByUserId { get; set; }
    public string? UpdatedByEmail { get; set; }
}
