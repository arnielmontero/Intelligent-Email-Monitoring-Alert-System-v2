using Iemas.Application.Agents.Dtos;
using Iemas.Domain.Cases;
using Microsoft.EntityFrameworkCore;
using static Iemas.Tests.Inquiries.InquiryHarness;

namespace Iemas.Tests.Inquiries;

/// <summary>
/// Agents whose Outlook uses the mailbox as POP keep their sent mail only on their PC, so the server's Sent
/// folder never shows their replies. The Windows Agent reads Outlook's Sent Items and reports them instead.
/// </summary>
public class OutlookSentItemsIngestTests
{
    private const string Agent1 = "agent1@sawo.com";
    private const string Agent2 = "agent2@sawo.com";
    private const string Customer = "anna@customer.example";

    private static AgentSentMessageDto Reply(string entryId, string from, string to, DateTimeOffset at, string body = "Here is our quotation.", string? inReplyTo = null) =>
        new(entryId, $"<{entryId}@outlook.test>", inReplyTo, inReplyTo, from, "Agent One", new List<string> { to }, null,
            "RE: Request for quotation", body, at, 0);

    private static async Task<InquiryHarness> WithInquiryAsync(params string[] mailboxes)
    {
        var h = new InquiryHarness(mailboxes);
        h.Receive(Agent1, Customer, "Request for quotation", "Hello, I would like to request a quotation for 20 heaters. How much per unit?", Monday0900, fromName: "Anna");
        await h.RunAsync(Monday0900.AddMinutes(1));
        return h;
    }

    [Fact]
    public async Task ReplyReportedByTheAgent_IsStoredAsOurReply_AndAdvancesTheInquiry()
    {
        using var h = await WithInquiryAsync(Agent1);

        var (stored, _) = await h.Sync.IngestFromAgentAsync(h.Agents[Agent1].Id,
            new[] { Reply("E1", Agent1, Customer, Monday0900.AddMinutes(30)).ToProviderMessage() }, CancellationToken.None);

        Assert.Equal(1, stored);
        var c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(CaseReplyStatus.Replied, c.ReplyStatus);
        Assert.Equal(InquiryStage.InitialResponseReceived, c.Stage);
        Assert.Equal(Monday0900.AddMinutes(30), c.FirstAgentResponseAt);
        var agentMail = await h.Db.CaseEmails.Include(ce => ce.EmailMessage).SingleAsync(ce => ce.CaseId == c.Id && ce.Role == ConversationRole.Agent);
        Assert.Equal("outlook:E1", agentMail.EmailMessage.ProviderMessageId);
    }

    [Fact]
    public async Task SameReplyReportedTwice_IsStoredOnce()
    {
        using var h = await WithInquiryAsync(Agent1);
        var reply = Reply("E1", Agent1, Customer, Monday0900.AddMinutes(30)).ToProviderMessage();

        await h.Sync.IngestFromAgentAsync(h.Agents[Agent1].Id, new[] { reply }, CancellationToken.None);
        var (second, _) = await h.Sync.IngestFromAgentAsync(h.Agents[Agent1].Id, new[] { reply }, CancellationToken.None);

        Assert.Equal(0, second);
        Assert.Equal(1, await h.Db.CaseEmails.CountAsync(ce => ce.Role == ConversationRole.Agent));
    }

    [Fact]
    public async Task ReplyAlsoFoundLaterInTheServerSentFolder_IsNotDuplicated()
    {
        using var h = await WithInquiryAsync(Agent1);
        await h.Sync.IngestFromAgentAsync(h.Agents[Agent1].Id,
            new[] { Reply("E1", Agent1, Customer, Monday0900.AddMinutes(30)).ToProviderMessage() }, CancellationToken.None);

        // The same email (same Message-ID) later shows up in the mailbox's Sent folder too (e.g. after a switch to IMAP).
        var viaOutlook = Reply("E1", Agent1, Customer, Monday0900.AddMinutes(30)).ToProviderMessage();
        await h.Sync.IngestFromAgentAsync(h.Agents[Agent1].Id, new[] { viaOutlook with { ProviderMessageId = "OTHER" } }, CancellationToken.None);

        Assert.Equal(1, await h.Db.CaseEmails.CountAsync(ce => ce.Role == ConversationRole.Agent));
    }

    [Fact]
    public async Task AnotherEmployeesAgent_CannotAttachMailToThisInquiry()
    {
        using var h = await WithInquiryAsync(Agent1, Agent2);

        var (stored, internalStored) = await h.Sync.IngestFromAgentAsync(h.Agents[Agent2].Id,
            new[] { Reply("E1", Agent1, Customer, Monday0900.AddMinutes(30)).ToProviderMessage() }, CancellationToken.None);

        Assert.Equal(0, stored + internalStored);
        Assert.Equal(InquiryStage.AwaitingInitialResponse, (await h.CaseAsync(Agent1, Customer)).Stage);
    }

    [Fact]
    public async Task MailSentFromAnotherOutlookAccount_IsIgnored()
    {
        using var h = await WithInquiryAsync(Agent1);

        var (stored, _) = await h.Sync.IngestFromAgentAsync(h.Agents[Agent1].Id,
            new[] { Reply("E1", "agent.private@gmail.example", Customer, Monday0900.AddMinutes(30)).ToProviderMessage() }, CancellationToken.None);

        Assert.Equal(0, stored);
    }

    [Fact]
    public async Task ReplyReportedAfterTheCustomerAlreadyWroteAgain_LeavesTheNextStepWithTheAgent()
    {
        using var h = await WithInquiryAsync(Agent1);
        // Agent replies at 09:30 (only in Outlook); the customer answers at 10:00 and that arrives first.
        h.Receive(Agent1, Customer, "RE: Request for quotation", "Thanks, and what about shipping to Riga?", Monday0900.AddHours(1));
        await h.RunAsync(Monday0900.AddHours(1).AddMinutes(1));

        await h.Sync.IngestFromAgentAsync(h.Agents[Agent1].Id,
            new[] { Reply("E1", Agent1, Customer, Monday0900.AddMinutes(30)).ToProviderMessage() }, CancellationToken.None);

        var c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(Monday0900.AddMinutes(30), c.FirstAgentResponseAt);
        Assert.True(Iemas.Application.Inquiries.InquiryLifecycle.AgentOwesNextStep(c.Stage), $"stage was {c.Stage}");
    }
}
