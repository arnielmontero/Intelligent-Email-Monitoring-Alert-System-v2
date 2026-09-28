using System.Net.Sockets;
using Iemas.Infrastructure.Providers;
using MailKit;
using MailKit.Net.Imap;
using MimeKit;

namespace Iemas.Tests.Providers;

/// <summary>
/// §81 "No lost Cases" — only failures about the message itself may be skipped as malformed.
/// Connection/timeout failures must stop the batch so the message is retried, never skipped.
/// </summary>
public class ImapMessageFailureTests
{
    [Fact]
    public void ParseAndFormatErrors_AreMessageLevel()
    {
        Assert.True(ImapEmailProviderAdapter.IsMessageLevelFailure(new FormatException("bad header")));
        Assert.True(ImapEmailProviderAdapter.IsMessageLevelFailure(new ParseException("bad MIME", 0, 0)));
    }

    [Fact]
    public void ConnectionAndTimeoutErrors_AreNotMessageLevel()
    {
        Assert.False(ImapEmailProviderAdapter.IsMessageLevelFailure(new ServiceNotConnectedException("The ImapClient is not connected.")));
        Assert.False(ImapEmailProviderAdapter.IsMessageLevelFailure(new OperationCanceledException("A task was canceled.")));
        Assert.False(ImapEmailProviderAdapter.IsMessageLevelFailure(new IOException("connection reset")));
        Assert.False(ImapEmailProviderAdapter.IsMessageLevelFailure(new SocketException()));
        Assert.False(ImapEmailProviderAdapter.IsMessageLevelFailure(new ImapProtocolException("unexpected token")));
    }
}
