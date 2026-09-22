using System.Security.Cryptography;
using System.Text;
using Iemas.Application.Common.Interfaces;
using Microsoft.Extensions.Options;

namespace Iemas.Infrastructure.Security;

public class CredentialEncryptionOptions
{
    public const string SectionName = "CredentialEncryption";

    /// <summary>Base64-encoded 256-bit key. Must be kept secret and out of source control (§16).</summary>
    public string Key { get; set; } = string.Empty;
    public string KeyId { get; set; } = "v1";
}

/// <summary>
/// AES-256-GCM authenticated encryption for mailbox/outbound credentials (§16).
/// The key never touches the database and is supplied via configuration/secrets only.
/// </summary>
public class AesGcmCredentialEncryptionService : ICredentialEncryptionService
{
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;

    private readonly byte[] _key;
    private readonly string _keyId;

    public AesGcmCredentialEncryptionService(IOptions<CredentialEncryptionOptions> options)
    {
        var config = options.Value;
        if (string.IsNullOrWhiteSpace(config.Key))
        {
            throw new InvalidOperationException("CredentialEncryption:Key is not configured.");
        }

        _key = Convert.FromBase64String(config.Key);
        if (_key.Length != 32)
        {
            throw new InvalidOperationException("CredentialEncryption:Key must decode to exactly 32 bytes (AES-256).");
        }

        _keyId = config.KeyId;
    }

    public EncryptedSecret Encrypt(string plaintext)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSizeBytes];

        using var aesGcm = new AesGcm(_key, TagSizeBytes);
        aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        return new EncryptedSecret(ciphertext, nonce, tag, _keyId);
    }

    public string Decrypt(EncryptedSecret secret)
    {
        var plaintextBytes = new byte[secret.Ciphertext.Length];

        using var aesGcm = new AesGcm(_key, TagSizeBytes);
        aesGcm.Decrypt(secret.Nonce, secret.Ciphertext, secret.Tag, plaintextBytes);

        return Encoding.UTF8.GetString(plaintextBytes);
    }
}
