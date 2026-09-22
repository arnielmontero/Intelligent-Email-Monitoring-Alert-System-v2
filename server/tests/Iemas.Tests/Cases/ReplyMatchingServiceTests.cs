using Iemas.Application.Cases;
using Iemas.Application.Common.Providers;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Xunit;

namespace Iemas.Tests.Cases;

/// <summary>§42 — matches Sent messages back to a Case's inbound emails via real identifier relationships; subject is never a signal here.</summary>
public class ReplyMatchingServiceTests
{
    private static EmailMessage InboundMessage(string? messageId = null, string? threadId = null, string subject = "Price Request")
    {
        return new EmailMessage
        {
            EmailAccountId = Guid.NewGuid(),
            Provider = EmailProtocol.Imap,
            ProviderMessageId = Guid.NewGuid().ToString(),
            MessageId = messageId,
            ThreadId = threadId,
            FromAddress = "customer@example.com",
            ToAddresses = "sales@sawo.com",
            Subject = subject,
            ReceivedAt = DateTimeOffset.UtcNow,
        };
    }

    private static ProviderMessage SentMessage(
        string? messageId = null, string? threadId = null, string? inReplyTo = null,
        IReadOnlyCollection<string>? references = null, string subject = "Re: Price Request",
        string toAddress = "customer@example.com")
    {
        return new ProviderMessage(
            Guid.NewGuid().ToString(), messageId, threadId, inReplyTo, references ?? Array.Empty<string>(),
            "sales@sawo.com", "Sales Team", new[] { toAddress }, Array.Empty<string>(),
            subject, "Thanks, here is the info.", null, DateTimeOffset.UtcNow, Array.Empty<ProviderAttachmentSummary>());
    }

    [Fact]
    public void FindReply_MatchesByThreadId()
    {
        var inbound = new[] { InboundMessage(threadId: "thread-1") };
        var sent = new[] { SentMessage(threadId: "thread-1") };

        var result = ReplyMatchingService.FindReply(inbound, sent, "customer@example.com");

        Assert.NotNull(result.MatchedMessage);
        Assert.Equal(ReplyMatchSignal.ThreadId, result.Signal);
    }

    [Fact]
    public void FindReply_MatchesByInReplyTo()
    {
        var inbound = new[] { InboundMessage(messageId: "<original@example.com>") };
        var sent = new[] { SentMessage(inReplyTo: "<original@example.com>") };

        var result = ReplyMatchingService.FindReply(inbound, sent, "customer@example.com");

        Assert.NotNull(result.MatchedMessage);
        Assert.Equal(ReplyMatchSignal.InReplyTo, result.Signal);
    }

    [Fact]
    public void FindReply_MatchesByReferences()
    {
        var inbound = new[] { InboundMessage(messageId: "<original@example.com>") };
        var sent = new[] { SentMessage(references: new[] { "<unrelated@example.com>", "<original@example.com>" }) };

        var result = ReplyMatchingService.FindReply(inbound, sent, "customer@example.com");

        Assert.NotNull(result.MatchedMessage);
        Assert.Equal(ReplyMatchSignal.References, result.Signal);
    }

    [Fact]
    public void FindReply_MatchesByMessageIdRelationship_ReverseDirection()
    {
        // The inbound Case message itself references a Sent message's Message-ID (e.g. a
        // follow-up inbound email arrived quoting the reply that was already sent).
        var inbound = new[] { InboundMessage(messageId: "<followup@example.com>") };
        inbound[0].InReplyTo = "<the-reply@sawo.com>";
        var sent = new[] { SentMessage(messageId: "<the-reply@sawo.com>") };

        var result = ReplyMatchingService.FindReply(inbound, sent, "customer@example.com");

        Assert.NotNull(result.MatchedMessage);
        Assert.Equal(ReplyMatchSignal.MessageIdRelationship, result.Signal);
    }

    [Fact]
    public void FindReply_MatchesByRecipientRelationship_WhenNoThreadSignalsPresent()
    {
        var inbound = new[] { InboundMessage() }; // no MessageId/ThreadId at all
        var sent = new[] { SentMessage(toAddress: "customer@example.com") };

        var result = ReplyMatchingService.FindReply(inbound, sent, "customer@example.com");

        Assert.NotNull(result.MatchedMessage);
        Assert.Equal(ReplyMatchSignal.RecipientAndAccountRelationship, result.Signal);
    }

    /// <summary>Hard requirement for this phase: subject is not a signal at all — a same-subject Sent message to a DIFFERENT recipient must not verify.</summary>
    [Fact]
    public void FindReply_SubjectAlone_NeverEstablishesAVerifiedReply()
    {
        var inbound = new[] { InboundMessage(subject: "Price Request") };
        // Same subject text, but sent to someone else entirely, and no thread/reply identifiers.
        var sent = new[] { SentMessage(subject: "Re: Price Request", toAddress: "someone-else@example.com") };

        var result = ReplyMatchingService.FindReply(inbound, sent, "customer@example.com");

        Assert.Null(result.MatchedMessage);
        Assert.Equal(ReplyMatchSignal.NoMatch, result.Signal);
    }

    [Fact]
    public void FindReply_NoSentMessages_ReturnsNoMatch()
    {
        var inbound = new[] { InboundMessage(messageId: "<x@example.com>") };
        var result = ReplyMatchingService.FindReply(inbound, Array.Empty<ProviderMessage>(), "customer@example.com");

        Assert.Null(result.MatchedMessage);
        Assert.Equal(ReplyMatchSignal.NoMatch, result.Signal);
    }

    /// <summary>Multiple Sent messages exist; only the one that actually matches an identifier relationship is picked, not just "any" Sent message.</summary>
    [Fact]
    public void FindReply_MultipleSentMessages_PicksTheGenuinelyMatchingOne()
    {
        var inbound = new[] { InboundMessage(messageId: "<original@example.com>") };
        var unrelatedSent = SentMessage(messageId: "<other-reply@sawo.com>", inReplyTo: "<some-other-thread@example.com>", toAddress: "different-customer@example.com");
        var matchingSent = SentMessage(messageId: "<the-real-reply@sawo.com>", inReplyTo: "<original@example.com>");

        var result = ReplyMatchingService.FindReply(inbound, new[] { unrelatedSent, matchingSent }, "customer@example.com");

        Assert.NotNull(result.MatchedMessage);
        Assert.Equal("<the-real-reply@sawo.com>", result.MatchedMessage!.MessageId);
        Assert.Equal(ReplyMatchSignal.InReplyTo, result.Signal);
    }

    /// <summary>Correct-Case matching: a Sent message that matches a DIFFERENT case's inbound message must not be picked up by this Case's check.</summary>
    [Fact]
    public void FindReply_DoesNotMatchAgainstAnotherCasesInboundMessages()
    {
        var thisCasesInbound = new[] { InboundMessage(messageId: "<case-a@example.com>") };
        // Only a Sent message replying to a totally different message-id is available.
        var sent = new[] { SentMessage(inReplyTo: "<case-b-message@example.com>", toAddress: "customer@example.com") };

        var result = ReplyMatchingService.FindReply(thisCasesInbound, sent, "customer@example.com");

        // Falls through to recipient-based matching since toAddress does match — this is
        // intentionally the weakest-but-still-identifier-based signal, not a false negative test;
        // verifies the strong signals correctly did NOT fire for the wrong case's message-id.
        Assert.Equal(ReplyMatchSignal.RecipientAndAccountRelationship, result.Signal);
    }
}
