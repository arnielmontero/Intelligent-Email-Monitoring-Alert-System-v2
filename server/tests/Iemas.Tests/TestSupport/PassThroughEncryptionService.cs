using System.Text;
using Iemas.Application.Common.Interfaces;

namespace Iemas.Tests.TestSupport;

/// <summary>
/// Not a real encryption implementation — used only where EmailIntakeService needs to decrypt
/// a stored credential and the test doesn't care about encryption itself (that's covered by
/// AesGcmCredentialEncryptionServiceTests). Ciphertext bytes here are just the UTF-8 plaintext.
/// </summary>
public class PassThroughEncryptionService : ICredentialEncryptionService
{
    public EncryptedSecret Encrypt(string plaintext) => new(Encoding.UTF8.GetBytes(plaintext), Array.Empty<byte>(), Array.Empty<byte>(), "test");

    public string Decrypt(EncryptedSecret secret) => Encoding.UTF8.GetString(secret.Ciphertext);
}
