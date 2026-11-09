using System.Text.RegularExpressions;
using Iemas.Application.Common.Interfaces;

namespace Iemas.Application.Inquiries;

/// <summary>
/// Q4 safeguards for storing the full text of agent replies:
/// - quoted history below the agent's own text is dropped (it is already stored as the customer's messages);
/// - card-number-like digit runs, IBAN-like strings and "password: ..." values are masked;
/// - the text is length-capped and encrypted with AES-GCM before it reaches the database.
/// Attachments are never stored (only their count, on the message row).
/// </summary>
public sealed class AgentReplyProtector
{
    public const int MaxStoredChars = 20_000;

    private static readonly Regex CardNumber = new(@"\b(?:\d[ -]?){13,19}\b", RegexOptions.Compiled);
    private static readonly Regex Iban = new(@"\b[A-Z]{2}\d{2}(?:[ ]?[A-Z0-9]{4}){3,7}\b", RegexOptions.Compiled);
    private static readonly Regex Secret = new(@"(?im)\b(password|passcode|pin|pwd|kennwort|salasana)\s*[:=]\s*\S+", RegexOptions.Compiled);
    private static readonly Regex QuoteHeader = new(@"(?im)^(?:On .{4,200}wrote:|-{2,}\s*Original Message\s*-{2,}|From:\s.+\r?\nSent:\s)", RegexOptions.Compiled);

    private readonly ICredentialEncryptionService _encryption;

    public AgentReplyProtector(ICredentialEncryptionService encryption)
    {
        _encryption = encryption;
    }

    /// <summary>The agent's own text, without quoted history, with sensitive values masked.</summary>
    public static string Sanitize(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        var text = body.Replace("\r\n", "\n");

        var quote = QuoteHeader.Match(text);
        if (quote.Success && quote.Index > 0) text = text[..quote.Index];
        text = string.Join('\n', text.Split('\n').Where(line => !line.TrimStart().StartsWith('>')));

        text = CardNumber.Replace(text, m => m.Value.Count(char.IsDigit) >= 13 ? "[number removed]" : m.Value);
        text = Iban.Replace(text, "[account number removed]");
        text = Secret.Replace(text, m => $"{m.Groups[1].Value}: [removed]");

        text = text.Trim();
        return text.Length > MaxStoredChars ? text[..MaxStoredChars] : text;
    }

    public string Protect(string? body)
    {
        var sealedSecret = _encryption.Encrypt(Sanitize(body));
        return string.Join(':', sealedSecret.KeyId,
            Convert.ToBase64String(sealedSecret.Nonce), Convert.ToBase64String(sealedSecret.Tag), Convert.ToBase64String(sealedSecret.Ciphertext));
    }

    /// <summary>Null when nothing is stored (erased by retention) or the key can no longer decrypt it.</summary>
    public string? Reveal(string? protectedBody)
    {
        if (string.IsNullOrEmpty(protectedBody)) return null;
        var parts = protectedBody.Split(':');
        if (parts.Length != 4) return null;
        try
        {
            return _encryption.Decrypt(new EncryptedSecret(
                Convert.FromBase64String(parts[3]), Convert.FromBase64String(parts[1]), Convert.FromBase64String(parts[2]), parts[0]));
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            return null;
        }
    }
}
