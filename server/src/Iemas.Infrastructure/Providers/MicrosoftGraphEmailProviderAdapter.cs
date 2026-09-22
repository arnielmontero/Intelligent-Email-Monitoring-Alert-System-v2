using Iemas.Application.Common.Providers;
using Iemas.Domain.Email;

namespace Iemas.Infrastructure.Providers;

/// <summary>
/// Requirements §14.1 — Microsoft Graph (Office 365) provider adapter placeholder.
/// Not implemented: Graph OAuth2 requires an Azure AD app registration (client ID/secret/tenant,
/// admin consent) that does not exist yet and is a business/IT decision outside this codebase.
/// Registered now so <see cref="EmailProtocol.MicrosoftGraph"/> is selectable in the CMS and the
/// adapter-resolution wiring is correct end-to-end; it fails loudly instead of silently pretending
/// to succeed, so nobody mistakes a stub for a working connection test.
/// </summary>
public class MicrosoftGraphEmailProviderAdapter : IEmailProviderAdapter
{
    public EmailProtocol Protocol => EmailProtocol.MicrosoftGraph;

    public Task<ProviderConnectionTestResult> TestConnectionAsync(EmailProviderConnectionSettings settings, CancellationToken cancellationToken)
    {
        return Task.FromResult(new ProviderConnectionTestResult(
            false,
            "Microsoft Graph provider is not yet implemented. Requires Azure AD app registration (§112 item 1/2, not yet frozen).",
            TimeSpan.Zero));
    }

    public Task<FetchInboxResult> FetchInboxMessagesAsync(
        EmailProviderConnectionSettings settings,
        uint? knownUidValidity,
        uint? afterUid,
        int maxMessages,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Microsoft Graph provider is not yet implemented (§112 item 1/2, not yet frozen).");
    }

    public Task<FetchSentResult> FetchSentMessagesAsync(
        EmailProviderConnectionSettings settings,
        DateTimeOffset since,
        int maxMessages,
        CancellationToken cancellationToken)
    {
        // §44 — an unimplemented provider must be reported as "could not check," never as a
        // silent "no reply found." FolderAccessible = false carries that distinction through to
        // the caller exactly like a real auth/connection failure would.
        return Task.FromResult(new FetchSentResult(
            false,
            "Microsoft Graph provider is not yet implemented (§112 item 1/2, not yet frozen).",
            Array.Empty<ProviderMessage>(),
            Array.Empty<(string, string)>()));
    }
}
