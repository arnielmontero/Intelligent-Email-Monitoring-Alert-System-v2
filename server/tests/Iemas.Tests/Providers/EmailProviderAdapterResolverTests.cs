using Iemas.Application.Common.Providers;
using Iemas.Domain.Email;
using Iemas.Infrastructure.Providers;
using Xunit;

namespace Iemas.Tests.Providers;

/// <summary>
/// §18 — SmtpEmailProviderAdapter was previously registered in DI as itself, never as
/// IEmailProviderAdapter, so it was unreachable through EmailProviderAdapterResolver.Resolve —
/// an Outbound EmailAccount's "Test Connection" had no working path. This confirms the fix: the
/// resolver can now find an adapter for EmailProtocol.Smtp, and that adapter genuinely refuses
/// (rather than silently no-ops) the two mailbox-read operations that don't apply to a send-only
/// protocol.
/// </summary>
public class EmailProviderAdapterResolverTests
{
    private static EmailProviderAdapterResolver CreateResolver() => new(new IEmailProviderAdapter[]
    {
        new SmtpEmailProviderAdapter(),
    });

    [Fact]
    public void Resolve_Smtp_ReturnsSmtpAdapter()
    {
        var resolver = CreateResolver();

        var adapter = resolver.Resolve(EmailProtocol.Smtp);

        Assert.IsType<SmtpEmailProviderAdapter>(adapter);
        Assert.Equal(EmailProtocol.Smtp, adapter.Protocol);
    }

    [Fact]
    public async Task SmtpAdapter_FetchInbox_ThrowsNotSupported()
    {
        var adapter = new SmtpEmailProviderAdapter();
        var settings = new EmailProviderConnectionSettings(EmailProtocol.Smtp, "smtp.example.com", 587, "STARTTLS", "user", EmailAuthMethod.Password, "secret");

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            adapter.FetchInboxMessagesAsync(settings, null, null, 25, CancellationToken.None));
    }

    [Fact]
    public async Task SmtpAdapter_FetchSent_ThrowsNotSupported()
    {
        var adapter = new SmtpEmailProviderAdapter();
        var settings = new EmailProviderConnectionSettings(EmailProtocol.Smtp, "smtp.example.com", 587, "STARTTLS", "user", EmailAuthMethod.Password, "secret");

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            adapter.FetchSentMessagesAsync(settings, DateTimeOffset.UtcNow.AddDays(-1), 25, CancellationToken.None));
    }
}
