using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;

namespace Iemas.Infrastructure.Providers;

/// <summary>
/// Phase 10 hardening — classifies a connect/authenticate failure from <see cref="ImapClient"/> so
/// <see cref="ImapEmailProviderAdapter"/> knows whether an in-run retry is worth attempting, the
/// same "let the failure itself say whether retrying can help" principle used for
/// <c>OpenRouterClassificationProvider</c>/<c>ClassificationFailureCategory</c>.
///
/// Deliberately a static function over a real <see cref="Exception"/>, not a method on the adapter
/// itself: MailKit's <see cref="ImapClient"/> does real socket I/O with no injectable seam, so it
/// cannot be driven by a fake in a unit test the way <c>HttpClient</c>/<c>FakeHttpMessageHandler</c>
/// could for OpenRouter. Keeping the exception-to-category mapping as a pure function lets it be
/// unit-tested directly against real exception instances; the retry loop that calls it stays
/// covered the way this adapter has always been verified — live, against a real GreenMail server
/// (see the Build Progress Tracker's Phase 2/3 sections).
/// </summary>
public enum ImapFailureCategory
{
    /// <summary>Network/DNS/socket/IO failure, or our own connect timeout — worth retrying.</summary>
    Transient,

    /// <summary>Wrong username/password/token, or a SASL mechanism rejection — retrying with the same credentials cannot succeed.</summary>
    AuthenticationFailure,

    /// <summary>TLS/certificate negotiation failure — could be a transient network hiccup during the handshake, but is at least as likely to be a genuine cert/config problem, so it gets a single retry rather than the full budget.</summary>
    TlsFailure,

    /// <summary>The server responded but rejected the request at the protocol level (e.g. a malformed command reply) — not a network problem, but not necessarily permanent either; treated as a single-retry case like TlsFailure.</summary>
    ProtocolError,

    /// <summary>Anything else unrecognized — treated conservatively as not worth retrying, so an unknown failure mode doesn't loop.</summary>
    Unknown,
}

public static class ImapFailureClassifier
{
    public static ImapFailureCategory Classify(Exception ex) => ex switch
    {
        // SaslException derives from MailKit.Security.AuthenticationException, so one arm covers both.
        AuthenticationException => ImapFailureCategory.AuthenticationFailure,

        SslHandshakeException => ImapFailureCategory.TlsFailure,
        System.Security.Authentication.AuthenticationException => ImapFailureCategory.TlsFailure,

        ImapProtocolException => ImapFailureCategory.ProtocolError,
        ImapCommandException => ImapFailureCategory.ProtocolError,

        SocketException => ImapFailureCategory.Transient,
        IOException => ImapFailureCategory.Transient,
        TimeoutException => ImapFailureCategory.Transient,
        OperationCanceledException => ImapFailureCategory.Transient,

        _ => ImapFailureCategory.Unknown,
    };

    /// <summary>Whether the next attempt (at zero-indexed <paramref name="attemptIndex"/>, before this one) is worth making for this category.</summary>
    public static bool IsRetryable(ImapFailureCategory category, int attemptIndex) => category switch
    {
        ImapFailureCategory.Transient => true,
        ImapFailureCategory.TlsFailure or ImapFailureCategory.ProtocolError => attemptIndex == 0,
        _ => false,
    };
}
