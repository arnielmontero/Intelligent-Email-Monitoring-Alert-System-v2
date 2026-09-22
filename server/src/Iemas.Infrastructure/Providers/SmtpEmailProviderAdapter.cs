using System.Diagnostics;
using Iemas.Application.Common.Providers;
using Iemas.Domain.Email;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace Iemas.Infrastructure.Providers;

/// <summary>
/// Requirements §18 — outbound (internal notification/escalation) email connection testing.
/// This adapter only verifies connectivity/authentication; it never sends a message during a test.
/// Actual sending is implemented in Phase 9 (Escalation) / Phase 8 (Notifications).
/// </summary>
public class SmtpEmailProviderAdapter
{
    public async Task<ProviderConnectionTestResult> TestConnectionAsync(EmailProviderConnectionSettings settings, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var client = new SmtpClient();

        try
        {
            var secureSocketOptions = MapEncryption(settings.Encryption);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));

            await client.ConnectAsync(settings.Host, settings.Port, secureSocketOptions, timeoutCts.Token);

            if (settings.AuthMethod == EmailAuthMethod.OAuth2)
            {
                await client.AuthenticateAsync(new SaslMechanismOAuth2(settings.Username, settings.Secret), timeoutCts.Token);
            }
            else
            {
                await client.AuthenticateAsync(settings.Username, settings.Secret, timeoutCts.Token);
            }

            await client.DisconnectAsync(true, cancellationToken);

            stopwatch.Stop();
            return new ProviderConnectionTestResult(true, null, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new ProviderConnectionTestResult(false, ex.Message, stopwatch.Elapsed);
        }
    }

    private static SecureSocketOptions MapEncryption(string encryption) => encryption.Trim().ToUpperInvariant() switch
    {
        "SSL/TLS" or "SSL" or "TLS" => SecureSocketOptions.SslOnConnect,
        "STARTTLS" => SecureSocketOptions.StartTls,
        "NONE" => SecureSocketOptions.None,
        _ => SecureSocketOptions.Auto
    };
}
