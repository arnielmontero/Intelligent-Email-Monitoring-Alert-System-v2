namespace Iemas.Application.Common.Interfaces;

public record EncryptedSecret(byte[] Ciphertext, byte[] Nonce, byte[] Tag, string KeyId);

/// <summary>
/// Requirements §16 — mailbox/outbound credentials must be encrypted at rest and never
/// returned in plaintext once saved (write-only after saving).
/// </summary>
public interface ICredentialEncryptionService
{
    EncryptedSecret Encrypt(string plaintext);
    string Decrypt(EncryptedSecret secret);
}
