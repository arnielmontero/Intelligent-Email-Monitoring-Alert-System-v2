using Iemas.Application.Common.Interfaces;
using Iemas.Domain.Email;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Iemas.Infrastructure.Email;

/// <summary>
/// G10 - delivers escalation email to management through the active Outbound email account
/// (Email Accounts → Purpose: Outbound, protocol SMTP). Internal email only: never used for
/// customer email. Failures are returned, never thrown, so the escalation decision itself stays
/// recorded and the delivery can be retried.
/// </summary>
public class SmtpEscalationEmailSender : IEscalationEmailSender
{
    private readonly IAppDbContext _db;
    private readonly ICredentialEncryptionService _encryption;
    private readonly ILogger<SmtpEscalationEmailSender> _logger;

    public SmtpEscalationEmailSender(IAppDbContext db, ICredentialEncryptionService encryption, ILogger<SmtpEscalationEmailSender> logger)
    {
        _db = db;
        _encryption = encryption;
        _logger = logger;
    }

    public async Task<OutboundEmailResult> SendAsync(OutboundEmail email, CancellationToken cancellationToken)
    {
        if (email.To.Count == 0) return new OutboundEmailResult(false, null, "No recipient email address.");

        var account = await _db.EmailAccounts.AsNoTracking().Include(a => a.Credential)
            .Where(a => a.Purpose == EmailAccountPurpose.Outbound && a.IsActive && a.Protocol == EmailProtocol.Smtp)
            .OrderBy(a => a.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (account?.Credential is null)
        {
            return new OutboundEmailResult(false, null, "No active Outbound (SMTP) email account with a credential is configured.");
        }

        try
        {
            var secret = _encryption.Decrypt(new(account.Credential.EncryptedSecret, account.Credential.Nonce, account.Credential.Tag, account.Credential.KeyId));

            var mime = new MimeMessage();
            mime.From.Add(new MailboxAddress(account.DisplayName ?? "IEMAS", account.EmailAddress));
            foreach (var to in email.To) mime.To.Add(new MailboxAddress(to.Name, to.Address));
            mime.Subject = email.Subject;
            mime.Body = new TextPart("plain") { Text = email.TextBody };
            mime.Headers.Add("Auto-Submitted", "auto-generated");
            mime.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId(account.EmailAddress.Split('@').Last());

            using var client = new SmtpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            var socket = account.Encryption.Trim().ToUpperInvariant() switch
            {
                "SSL/TLS" or "SSL" or "TLS" => SecureSocketOptions.SslOnConnect,
                "STARTTLS" => SecureSocketOptions.StartTls,
                "NONE" => SecureSocketOptions.None,
                _ => SecureSocketOptions.Auto,
            };
            await client.ConnectAsync(account.Host, account.Port, socket, timeout.Token);
            if (account.AuthMethod == EmailAuthMethod.OAuth2)
            {
                await client.AuthenticateAsync(new SaslMechanismOAuth2(account.Username, secret), timeout.Token);
            }
            else if (!string.IsNullOrEmpty(account.Username))
            {
                await client.AuthenticateAsync(account.Username, secret, timeout.Token);
            }
            await client.SendAsync(mime, timeout.Token);
            await client.DisconnectAsync(true, cancellationToken);

            return new OutboundEmailResult(true, mime.MessageId, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new OutboundEmailResult(false, null, "Sending timed out after 30s.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Escalation email could not be sent through {Mailbox}", account.EmailAddress);
            return new OutboundEmailResult(false, null, ex.Message);
        }
    }
}
