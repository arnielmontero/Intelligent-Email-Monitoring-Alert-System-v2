using Iemas.Domain.Common;

namespace Iemas.Domain.Email;

/// <summary>
/// Requirements §16 — credential security. Physically separated from <see cref="EmailAccount"/> so
/// that no query/projection against the account table can accidentally include a secret column.
/// Stores only ciphertext + the metadata needed to decrypt/rotate; the plaintext secret is never
/// persisted, logged, or returned by any API.
/// </summary>
public class EmailCredential : Entity
{
    public Guid EmailAccountId { get; set; }
    public EmailAccount EmailAccount { get; set; } = null!;

    /// <summary>AES-GCM ciphertext of the password / app password / OAuth refresh token.</summary>
    public byte[] EncryptedSecret { get; set; } = Array.Empty<byte>();
    public byte[] Nonce { get; set; } = Array.Empty<byte>();
    public byte[] Tag { get; set; } = Array.Empty<byte>();

    /// <summary>Key identifier/version, so the encryption key can be rotated without breaking old rows.</summary>
    public string KeyId { get; set; } = "v1";

    public DateTimeOffset? RotatedAt { get; set; }
}
