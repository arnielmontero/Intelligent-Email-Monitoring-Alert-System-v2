using System.Security.Cryptography;
using Iemas.Infrastructure.Security;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iemas.Tests.Security;

public class AesGcmCredentialEncryptionServiceTests
{
    private static AesGcmCredentialEncryptionService CreateService(string? key = null)
    {
        var options = Options.Create(new CredentialEncryptionOptions
        {
            Key = key ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            KeyId = "v1"
        });
        return new AesGcmCredentialEncryptionService(options);
    }

    [Fact]
    public void EncryptThenDecrypt_RoundTripsToTheOriginalPlaintext()
    {
        var service = CreateService();
        const string plaintext = "SuperSecretPassword123!";

        var encrypted = service.Encrypt(plaintext);
        var decrypted = service.Decrypt(encrypted);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Encrypt_NeverProducesCiphertextEqualToThePlaintextBytes()
    {
        var service = CreateService();
        var encrypted = service.Encrypt("password");

        Assert.NotEqual("password", System.Text.Encoding.UTF8.GetString(encrypted.Ciphertext));
    }

    /// <summary>
    /// Requirements §16 — a corrupted/tampered stored credential must fail decryption loudly
    /// (AES-GCM authentication tag check), never silently return garbage as if it were the secret.
    /// Reproduces the bug found via live Docker verification where a tampered Tag crashed the
    /// request with an unhandled CryptographicException instead of failing cleanly.
    /// </summary>
    [Fact]
    public void Decrypt_ThrowsCryptographicException_WhenTagIsTampered()
    {
        var service = CreateService();
        var encrypted = service.Encrypt("password");
        var tamperedTag = new byte[encrypted.Tag.Length];

        var tampered = encrypted with { Tag = tamperedTag };

        Assert.ThrowsAny<CryptographicException>(() => service.Decrypt(tampered));
    }

    [Fact]
    public void Decrypt_ThrowsCryptographicException_WhenCiphertextIsTampered()
    {
        var service = CreateService();
        var encrypted = service.Encrypt("password");
        var tamperedCiphertext = encrypted.Ciphertext.ToArray();
        tamperedCiphertext[0] ^= 0xFF;

        var tampered = encrypted with { Ciphertext = tamperedCiphertext };

        Assert.ThrowsAny<CryptographicException>(() => service.Decrypt(tampered));
    }

    [Fact]
    public void Decrypt_Fails_WhenUsingADifferentKeyThanWasUsedToEncrypt()
    {
        var encryptingService = CreateService();
        var encrypted = encryptingService.Encrypt("password");

        var differentKeyService = CreateService(); // different random key

        Assert.ThrowsAny<CryptographicException>(() => differentKeyService.Decrypt(encrypted));
    }

    [Fact]
    public void Constructor_Throws_WhenKeyIsNotConfigured()
    {
        var options = Options.Create(new CredentialEncryptionOptions { Key = "", KeyId = "v1" });

        Assert.Throws<InvalidOperationException>(() => new AesGcmCredentialEncryptionService(options));
    }

    [Fact]
    public void Constructor_Throws_WhenKeyIsNot32Bytes()
    {
        var options = Options.Create(new CredentialEncryptionOptions
        {
            Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)), // AES-128, not 256
            KeyId = "v1"
        });

        Assert.Throws<InvalidOperationException>(() => new AesGcmCredentialEncryptionService(options));
    }
}
