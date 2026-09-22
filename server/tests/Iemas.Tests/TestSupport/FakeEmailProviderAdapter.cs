using Iemas.Application.Common.Providers;
using Iemas.Domain.Email;

namespace Iemas.Tests.TestSupport;

/// <summary>
/// In-process fake used for EmailIntakeService unit tests. Live provider behavior (real IMAP
/// protocol, real TLS validation, real auth) is verified separately against GreenMail in Docker —
/// see IEMAS_Build_Progress_Tracker.md Phase 3 section — this fake only exercises IEMAS's own
/// fetch/normalize/persist/duplicate-check/failure-isolation logic in EmailIntakeService.
/// </summary>
public class FakeEmailProviderAdapter : IEmailProviderAdapter
{
    public EmailProtocol Protocol => EmailProtocol.Imap;

    public Func<EmailProviderConnectionSettings, uint?, uint?, int, CancellationToken, Task<FetchInboxResult>>? FetchBehavior { get; set; }
    public Func<EmailProviderConnectionSettings, CancellationToken, Task<ProviderConnectionTestResult>>? TestConnectionBehavior { get; set; }
    public Func<EmailProviderConnectionSettings, DateTimeOffset, int, CancellationToken, Task<FetchSentResult>>? FetchSentBehavior { get; set; }

    public Task<ProviderConnectionTestResult> TestConnectionAsync(EmailProviderConnectionSettings settings, CancellationToken cancellationToken)
    {
        return TestConnectionBehavior?.Invoke(settings, cancellationToken)
            ?? Task.FromResult(new ProviderConnectionTestResult(true, null, TimeSpan.Zero));
    }

    public Task<FetchInboxResult> FetchInboxMessagesAsync(EmailProviderConnectionSettings settings, uint? knownUidValidity, uint? afterUid, int maxMessages, CancellationToken cancellationToken)
    {
        if (FetchBehavior is null)
        {
            return Task.FromResult(new FetchInboxResult(1, afterUid, Array.Empty<ProviderMessage>(), Array.Empty<(uint, string)>()));
        }

        return FetchBehavior(settings, knownUidValidity, afterUid, maxMessages, cancellationToken);
    }

    public Task<FetchSentResult> FetchSentMessagesAsync(EmailProviderConnectionSettings settings, DateTimeOffset since, int maxMessages, CancellationToken cancellationToken)
    {
        if (FetchSentBehavior is null)
        {
            return Task.FromResult(new FetchSentResult(true, null, Array.Empty<ProviderMessage>(), Array.Empty<(string, string)>()));
        }

        return FetchSentBehavior(settings, since, maxMessages, cancellationToken);
    }
}

public class FakeEmailProviderAdapterResolver : IEmailProviderAdapterResolver
{
    private readonly IEmailProviderAdapter _adapter;

    public FakeEmailProviderAdapterResolver(IEmailProviderAdapter adapter)
    {
        _adapter = adapter;
    }

    public IEmailProviderAdapter Resolve(EmailProtocol protocol) => _adapter;
}
