using System.Net.Sockets;
using Iemas.Infrastructure.Providers;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Xunit;

namespace Iemas.Tests.Providers;

/// <summary>
/// Phase 10 hardening — <see cref="ImapFailureClassifier"/> is a pure function over real exception
/// instances, deliberately separated from <see cref="Iemas.Infrastructure.Providers.ImapEmailProviderAdapter"/>'s
/// actual connect/retry loop because MailKit's <c>ImapClient</c> does real socket I/O with no
/// injectable seam — it cannot be driven by a fake the way OpenRouter's <c>HttpClient</c> could.
/// The retry *loop* itself stays covered by this adapter's established live-verification-only
/// pattern (real GreenMail, see the Build Progress Tracker's Phase 2/3/6 sections); this test class
/// covers the classification decision the loop depends on.
/// </summary>
public class ImapFailureClassifierTests
{
    [Fact]
    public void Classify_AuthenticationException_IsAuthenticationFailure()
    {
        var ex = new AuthenticationException("Invalid credentials.");
        Assert.Equal(ImapFailureCategory.AuthenticationFailure, ImapFailureClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_SocketException_IsTransient()
    {
        var ex = new SocketException((int)SocketError.ConnectionRefused);
        Assert.Equal(ImapFailureCategory.Transient, ImapFailureClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_IOException_IsTransient()
    {
        var ex = new IOException("Connection reset by peer.");
        Assert.Equal(ImapFailureCategory.Transient, ImapFailureClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_OperationCanceledException_IsTransient()
    {
        // Our own CancelAfter(timeout) surfaces as this — worth retrying, same as OpenRouter's timeout handling.
        var ex = new OperationCanceledException("The operation was canceled.");
        Assert.Equal(ImapFailureCategory.Transient, ImapFailureClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_ImapProtocolException_IsProtocolError()
    {
        var ex = new ImapProtocolException("Unexpected token in IMAP response.");
        Assert.Equal(ImapFailureCategory.ProtocolError, ImapFailureClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_UnrecognizedException_IsUnknown()
    {
        var ex = new InvalidOperationException("Something unrelated to IMAP went wrong.");
        Assert.Equal(ImapFailureCategory.Unknown, ImapFailureClassifier.Classify(ex));
    }

    [Theory]
    [InlineData(ImapFailureCategory.Transient, 0, true)]
    [InlineData(ImapFailureCategory.Transient, 1, true)]
    [InlineData(ImapFailureCategory.Transient, 5, true)]
    public void IsRetryable_Transient_AlwaysRetryable(ImapFailureCategory category, int attemptIndex, bool expected)
    {
        Assert.Equal(expected, ImapFailureClassifier.IsRetryable(category, attemptIndex));
    }

    [Theory]
    [InlineData(ImapFailureCategory.TlsFailure, 0, true)]
    [InlineData(ImapFailureCategory.TlsFailure, 1, false)]
    [InlineData(ImapFailureCategory.ProtocolError, 0, true)]
    [InlineData(ImapFailureCategory.ProtocolError, 1, false)]
    public void IsRetryable_TlsOrProtocolError_OnlyFirstAttempt(ImapFailureCategory category, int attemptIndex, bool expected)
    {
        Assert.Equal(expected, ImapFailureClassifier.IsRetryable(category, attemptIndex));
    }

    [Theory]
    [InlineData(ImapFailureCategory.AuthenticationFailure, 0)]
    [InlineData(ImapFailureCategory.AuthenticationFailure, 1)]
    [InlineData(ImapFailureCategory.Unknown, 0)]
    [InlineData(ImapFailureCategory.Unknown, 1)]
    public void IsRetryable_AuthenticationFailureOrUnknown_NeverRetryable(ImapFailureCategory category, int attemptIndex)
    {
        Assert.False(ImapFailureClassifier.IsRetryable(category, attemptIndex));
    }
}
