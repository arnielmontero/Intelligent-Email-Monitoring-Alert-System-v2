using System.Diagnostics;
using Iemas.Application.Common.Providers;
using Iemas.Domain.Email;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace Iemas.Infrastructure.Providers;

/// <summary>
/// Requirements §18 — outbound (internal notification/escalation) email connection testing.
/// This adapter only verifies connectivity/authentication; it never sends a message during a test.
/// Implements IEmailProviderAdapter (rather than standing alone) so EmailProviderAdapterResolver
/// can resolve it for EmailProtocol.Smtp the same way it resolves Imap/MicrosoftGraph — before this,
/// this class was registered in DI but never reachable through the normal resolve path, so an
/// Outbound EmailAccount's "Test Connection" had no working adapter to route to. Send-only: the
/// Fetch* methods are genuinely not applicable for an outbound-only SMTP account (never called by
/// any caller, since Purpose=Outbound accounts are never passed to email intake/reply verification).
/// </summary>
public class SmtpEmailProviderAdapter : IEmailProviderAdapter
{
    public EmailProtocol Protocol => EmailProtocol.Smtp;

    public Task<FetchInboxResult> FetchInboxMessagesAsync(EmailProviderConnectionSettings settings, uint? knownUidValidity, uint? afterUid, int maxMessages, CancellationToken cancellationToken)
        => throw new NotSupportedException("SMTP is a send-only protocol; it has no inbox to fetch. Outbound accounts are never used for email intake.");

    public Task<FetchSentResult> FetchSentMessagesAsync(EmailProviderConnectionSettings settings, DateTimeOffset since, int maxMessages, CancellationToken cancellationToken)
        => throw new NotSupportedException("SMTP is a send-only protocol; it has no Sent folder to fetch. Outbound accounts are never used for reply verification.");

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
