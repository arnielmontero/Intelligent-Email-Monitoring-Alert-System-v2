using Iemas.Application.Inquiries;
using Iemas.Application.Operations;
using Iemas.Domain.Cases;
using Iemas.Domain.Operations;
using Iemas.Domain.Reminders;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using static Iemas.Tests.Inquiries.InquiryHarness;

namespace Iemas.Tests.Inquiries;

public class BusinessCalendarTests
{
    private static readonly BusinessCalendar Calendar = new(TimeZoneInfo.Utc, TimeSpan.FromHours(8), TimeSpan.FromHours(17), true,
        new[] { new DateOnly(2026, 11, 4) }); // Wednesday holiday

    [Fact]
    public void FourBusinessHours_WithinTheDay() =>
        Assert.Equal(Day(0, 13), Calendar.AddBusinessHours(Day(0, 9), 4));

    [Fact]
    public void Overnight_TimeOutsideBusinessHoursDoesNotCount() =>
        Assert.Equal(Day(1, 10), Calendar.AddBusinessHours(Day(0, 16), 3)); // 1h Monday + 2h Tuesday

    [Fact]
    public void EmailArrivingAtNight_CountsFromNextMorning() =>
        Assert.Equal(Day(1, 12), Calendar.AddBusinessHours(Day(0, 22), 4));

    [Fact]
    public void Holiday_IsSkipped() =>
        Assert.Equal(Day(3, 9), Calendar.AddBusinessDays(Day(1, 9), 1)); // Tuesday + 1 business day skips Wednesday's holiday

    [Fact]
    public void Weekend_IsSkipped() =>
        Assert.Equal(Day(7, 9), Calendar.AddBusinessDays(Day(4, 9), 1)); // Friday + 1 business day = Monday

    [Fact]
    public void FiveBusinessDayTarget_FromMondayMorning_IsNextMonday() =>
        Assert.Equal(Day(8, 9), Calendar.AddBusinessDays(Day(0, 9), 5)); // the Wednesday holiday pushes it to Tuesday
}

public class AgentReplyProtectorTests
{
    [Fact]
    public void QuotedHistory_IsDropped_AndSensitiveValuesMasked()
    {
        var text = AgentReplyProtector.Sanitize(
            "Your card 4111 1111 1111 1111 was refunded. Portal password: Hunter2!\n\nOn Mon, Anna wrote:\n> my old message");
        Assert.DoesNotContain("4111", text);
        Assert.DoesNotContain("Hunter2", text);
        Assert.DoesNotContain("my old message", text);
        Assert.Contains("[number removed]", text);
        Assert.Contains("password: [removed]", text);
    }

    [Fact]
    public void Protect_ThenReveal_RoundTrips_AndStoresNoPlaintextColumn()
    {
        var protector = new AgentReplyProtector(new PassThroughEncryptionService());
        var sealedText = protector.Protect("Here is your quotation.");
        Assert.Equal("Here is your quotation.", protector.Reveal(sealedText));
        Assert.Null(protector.Reveal(null));
        Assert.Null(protector.Reveal("not-a-sealed-value"));
    }
}

public class SupervisionSwitchAndActionsTests
{
    private const string Agent1 = "agent1@sawo.com";
    private const string Customer = "anna@customer.example";

    [Fact]
    public async Task SupervisionOff_UsesTheOlderReminderCycle_AndTheEngineDoesNothing()
    {
        using var h = new InquiryHarness(Agent1);
        h.Db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.SupervisionEnabled, Value = "false" });
        h.Db.ReminderPolicies.Add(new ReminderPolicy { Name = "Default", Enabled = true, IsDefault = true, RestrictToBusinessHours = false, ExcludeWeekends = false });
        await h.Db.SaveChangesAsync();

        h.Receive(Agent1, Customer, "Request for quotation", "I would like to request a quotation. How much?", Monday0900);
        var result = await h.RunAsync(Day(5, 9));

        Assert.Equal(0, result.Considered);
        Assert.Equal(1, await h.Db.Reminders.CountAsync(r => r.Status == ReminderStatus.Scheduled && r.Trigger == ReminderTrigger.InitialActionRequired));
    }

    [Fact]
    public async Task RemindLater_AfterTheAgentReplied_IsStillScheduled()
    {
        using var h = new InquiryHarness(Agent1);
        h.Db.ReminderPolicies.Add(new ReminderPolicy { Name = "Default", Enabled = true, IsDefault = true });
        await h.Db.SaveChangesAsync();
        var first = h.Receive(Agent1, Customer, "Request for quotation", "I would like to request a quotation. How much?", Monday0900);
        await h.RunAsync(Monday0900.AddMinutes(1));
        h.Send(Agent1, Customer, "Re: Request for quotation", "I'll prepare it and send it tomorrow.", Day(0, 10), inReplyTo: first);
        await h.RunAsync(Day(0, 10, 5));
        var c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(CaseReplyStatus.Replied, c.ReplyStatus);

        var result = await h.AgentActions.SubmitActionAsync(Guid.NewGuid(), h.Agents[Agent1].Id,
            new Iemas.Application.Agents.Dtos.SubmitCaseActionRequest("later-1", c.Id, Iemas.Domain.Agents.CaseActionType.RemindLater, null, Day(0, 10, 6), Day(1, 9)),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(1, await h.Db.Reminders.CountAsync(r => r.Trigger == ReminderTrigger.EmployeeRequested && r.Status == ReminderStatus.Scheduled));
    }

    [Fact]
    public async Task Reassign_MovesOwnership_RecordsHistory_AndNewOwnerGetsTheReminders()
    {
        using var h = new InquiryHarness(Agent1, "agent2@sawo.com");
        h.Receive(Agent1, Customer, "Request for quotation", "I would like to request a quotation. How much?", Monday0900);
        await h.RunAsync(Monday0900.AddMinutes(1));
        var c = await h.CaseAsync(Agent1, Customer);

        var overview = new InquiryOverviewService(h.Db, new AgentReplyProtector(new PassThroughEncryptionService()));
        var result = await overview.ReassignAsync(c.Id, h.Agents["agent2@sawo.com"].Id, "admin@sawo.com", CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(1, await h.Db.CaseEvents.CountAsync(e => e.CaseId == c.Id && e.EventType == CaseEventType.Reassigned));
        await h.RunAsync(Day(0, 13));
        await h.RunAsync(Day(0, 15));
        Assert.Contains("Reminder", h.PopUpTitlesFor("agent2@sawo.com"));
        Assert.DoesNotContain("Reminder", h.PopUpTitlesFor(Agent1));
    }

    [Fact]
    public async Task SetupCheck_ReportsWhatIsMissing_AndNeverReturnsASecret()
    {
        using var db = TestDbContext.CreateNew();
        var overview = new InquiryOverviewService(db, new AgentReplyProtector(new PassThroughEncryptionService()),
            new StaticAiProviderConnectionResolver(null));

        var empty = await overview.GetSetupCheckAsync(CancellationToken.None);
        Assert.False(empty.Single(i => i.Key == "ai-key").Ok);
        Assert.False(empty.Single(i => i.Key == "agent-mailboxes").Ok);
        Assert.False(empty.Single(i => i.Key == "outbound-smtp").Ok);
        Assert.True(empty.Single(i => i.Key == "internal-domains").Ok);   // default sawo.com
        Assert.True(empty.Single(i => i.Key == "supervision").Ok);
        Assert.All(empty.Where(i => !i.Ok), i => Assert.StartsWith("/", i.Link));

        var withKey = new InquiryOverviewService(db, new AgentReplyProtector(new PassThroughEncryptionService()),
            new StaticAiProviderConnectionResolver("sk-or-SECRET-VALUE-123456"));
        var items = await withKey.GetSetupCheckAsync(CancellationToken.None);
        Assert.True(items.Single(i => i.Key == "ai-key").Ok);
        Assert.DoesNotContain("SECRET", string.Join(" ", items.Select(i => i.Detail)));
    }

    [Fact]
    public async Task List_AwaitingAgentAndActiveBuckets_SelectTheRightStages()
    {
        using var h = new InquiryHarness(Agent1);
        var first = h.Receive(Agent1, Customer, "Request for quotation", "I would like to request a quotation. How much?", Monday0900);
        h.Receive(Agent1, "bo@other.example", "Pricing", "Could you send your price list? How much is delivery?", Monday0900);
        await h.RunAsync(Monday0900.AddMinutes(1));
        h.Send(Agent1, Customer, "Re: Request for quotation", "Which model do you need?", Day(0, 10), inReplyTo: first);
        await h.RunAsync(Day(0, 10, 5));
        var overview = new InquiryOverviewService(h.Db, new AgentReplyProtector(new PassThroughEncryptionService()));

        var awaitingAgent = await overview.ListAsync("awaiting-agent", null, null, 1, 50, CancellationToken.None);
        var active = await overview.ListAsync("active", null, null, 1, 50, CancellationToken.None);

        Assert.Equal("bo@other.example", Assert.Single(awaitingAgent.Items).Customer);
        Assert.Equal(Customer, Assert.Single(active.Items).Customer);
    }

    [Fact]
    public async Task Overview_CountsInquiriesPerAgentAndStage()
    {
        using var h = new InquiryHarness(Agent1, "agent2@sawo.com");
        h.Receive(Agent1, Customer, "Request for quotation", "I would like to request a quotation. How much?", Monday0900);
        var other = h.Receive("agent2@sawo.com", "bo@other.example", "Request for quotation", "Please request a quotation. How much?", Monday0900);
        await h.RunAsync(Monday0900.AddMinutes(1));
        h.Send("agent2@sawo.com", "bo@other.example", "Re: Request for quotation", "Which model do you need?", Day(0, 10), inReplyTo: other);
        await h.RunAsync(Day(0, 10, 5));

        var overview = await new InquiryOverviewService(h.Db, new AgentReplyProtector(new PassThroughEncryptionService())).GetOverviewAsync(CancellationToken.None);

        Assert.Equal(2, overview.Totals.OpenTotal);
        Assert.Equal(1, overview.Totals.AwaitingInitialResponse);
        Assert.Equal(1, overview.Totals.AwaitingCustomer);
        Assert.Equal(1, overview.Agents.Single(a => a.EmployeeId == h.Agents[Agent1].Id).AwaitingInitialResponse);
        Assert.Equal(1, overview.Agents.Single(a => a.EmployeeId == h.Agents["agent2@sawo.com"].Id).AwaitingCustomer);
    }
}
