using Iemas.Application.Agents.Dtos;
using Iemas.Application.Inquiries;
using Iemas.Application.Operations;
using Iemas.Domain.Agents;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Iemas.Domain.Escalations;
using Iemas.Domain.Operations;
using Iemas.Domain.Reminders;
using Microsoft.EntityFrameworkCore;
using static Iemas.Tests.Inquiries.InquiryHarness;

namespace Iemas.Tests.Inquiries;

/// <summary>
/// Phase 9 - the ten required scenarios, run end to end through the real pipeline (intake →
/// Zettelkasten/AI → Case → Sent-folder sync → supervision engine) with an explicit clock.
/// Calendar: Mon-Fri 08:00-17:00 UTC. Defaults (Q1): first response 4 business h, reminders every
/// 2 business h, escalation after 2 reminders, idle 1 business day, customer wait 3 calendar days,
/// resolution target 5 business days.
/// </summary>
public class InquiryLifecycleEndToEndTests
{
    private const string Agent1 = "agent1@sawo.com";
    private const string Agent2 = "agent2@sawo.com";
    private const string Agent3 = "agent3@sawo.com";
    private const string Customer = "anna@customer.example";

    // ---------------------------------------------------------------- Scenarios 1, 2, 3

    [Fact]
    public async Task S1_LegitimateInquiry_IsDetected_Recorded_AssignedAndAgentNotified()
    {
        using var h = new InquiryHarness(Agent1);
        h.Receive(Agent1, Customer, "Request for quotation", "Hello, I would like to request a quotation for 20 heaters. How much per unit?", Monday0900, fromName: "Anna");

        await h.RunAsync(Monday0900.AddMinutes(1));

        var c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(h.Agents[Agent1].Id, c.OwnerEmployeeId);
        Assert.Equal(InquiryStage.AwaitingInitialResponse, c.Stage);
        Assert.Equal(CaseWorkStatus.ActionRequired, c.WorkStatus);
        Assert.Equal("Request for quotation", c.Subject);
        Assert.Equal(Monday0900, c.FirstEmailReceivedAt);
        Assert.Contains("New Email", h.PopUpTitlesFor(Agent1));
        Assert.Empty(h.Ai.CallsByModel); // clear inquiry: decided by the Zettelkasten filter alone
        Assert.NotNull(c.ResolutionDueAt);
        Assert.Equal(Day(7, 9), c.ResolutionDueAt); // 5 business days later: next Monday 09:00
    }

    [Fact]
    public async Task S2_NonInquiryEmail_DoesNotCreateAnInquiry()
    {
        using var h = new InquiryHarness(Agent1);
        h.Receive(Agent1, "newsletter@vendor.example", "November news", "Our latest news. Unsubscribe at any time. View this email in your browser.", Monday0900);
        h.Receive(Agent1, "noreply@bank.example", "Payment confirmation", "Payment received for invoice 1043. This is an automated message.", Monday0900);
        h.Receive(Agent1, "it@sawo.com", "Server maintenance tonight", "Can you log off by 18:00? How much downtime is ok?", Monday0900);

        await h.RunAsync(Monday0900.AddMinutes(1));

        Assert.Empty(await h.Db.Cases.ToListAsync());
        Assert.Empty(h.Ai.CallsByModel);
        Assert.Equal(3, await h.Db.EmailMessages.CountAsync(m => m.ProcessingStatus == EmailProcessingStatus.Processed));
    }

    [Fact]
    public async Task S3_UncertainEmail_IsSentToAi_AndClassifiedAsInquiry()
    {
        using var h = new InquiryHarness(Agent1);
        h.Receive(Agent1, "lee@prospect.example", "Upcoming project",
            "I visited your website and wanted to know whether your team could support us on an upcoming project.", Monday0900);

        await h.RunAsync(Monday0900.AddMinutes(1));

        Assert.Single(h.Ai.CallsByModel);
        var c = await h.CaseAsync(Agent1, "lee@prospect.example");
        Assert.Equal(InquiryStage.AwaitingInitialResponse, c.Stage);
        var classification = await h.Db.EmailClassifications.SingleAsync();
        Assert.Contains("Uncertain", classification.DeterministicFilterReason);
        Assert.Equal("PROJECT_INQUIRY", classification.Category);
    }

    // ---------------------------------------------------------------- Scenarios 4 + 8

    [Fact]
    public async Task S4_S8_AgentNeverResponds_RemindedTwice_ThenManagementEscalation_ThenNextLevel()
    {
        using var h = new InquiryHarness(Agent1);
        h.Receive(Agent1, Customer, "Request for quotation", "I would like to request a quotation. What is the price?", Monday0900, fromName: "Anna");
        await h.RunAsync(Monday0900.AddMinutes(1));

        await h.RunAsync(Day(0, 12, 59));
        Assert.Equal(0, await h.Db.Reminders.CountAsync());

        // 13:00 - the 4-business-hour response deadline passes (timeline note, no pop-up yet).
        await h.RunAsync(Day(0, 13, 0));
        var c = await h.CaseAsync(Agent1, Customer);
        Assert.NotNull(c.EpisodeDeadlineMissedAt);
        Assert.Equal(0, await h.Db.Reminders.CountAsync());

        // 15:00 - first reminder (2 business hours after the deadline).
        await h.RunAsync(Day(0, 15, 0));
        Assert.Equal(1, await h.Db.Reminders.CountAsync(r => r.Status == ReminderStatus.Sent));
        Assert.Equal("Reminder", h.PopUpTitlesFor(Agent1).Last());

        // 17:00 - second reminder, carrying the escalation warning.
        await h.RunAsync(Day(0, 17, 0));
        Assert.Equal(2, await h.Db.Reminders.CountAsync(r => r.Status == ReminderStatus.Sent));
        Assert.Equal("Escalation Warning", h.PopUpTitlesFor(Agent1).Last());

        // Overnight and early Tuesday: business time is not counting, nothing happens.
        await h.RunAsync(Day(1, 9, 59));
        Assert.Empty(h.ManagementEmail.Sent);

        // Tuesday 10:00 - one interval after the 2nd reminder: management escalation (level 1, the supervisor).
        await h.RunAsync(Day(1, 10, 0));
        var email = Assert.Single(h.ManagementEmail.Sent);
        Assert.Equal(h.Manager.Email, Assert.Single(email.To).Address);
        Assert.Contains("Anna <anna@customer.example>", email.TextBody);                // customer
        Assert.Contains("Request for quotation", email.TextBody);                       // original subject
        Assert.Contains("2026-11-02 09:00", email.TextBody);                            // original date
        Assert.Contains("Agent agent1 <agent1@sawo.com>", email.TextBody);              // assigned agent
        Assert.Contains("Awaiting initial response", email.TextBody);                   // current status
        Assert.Contains("Agent has replied:  No", email.TextBody);                      // responded?
        Assert.Contains("Last activity:", email.TextBody);                              // last activity
        Assert.Contains("Inactive for:", email.TextBody);                               // inactivity duration
        Assert.Contains("Previous reminders: 2", email.TextBody);                       // reminders
        Assert.Contains("No initial response within 4 business hour(s)", email.TextBody); // reason
        c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(CaseWorkStatus.Escalated, c.WorkStatus);
        Assert.Equal(InquiryStage.AwaitingInitialResponse, c.Stage);
        Assert.Equal(h.Agents[Agent1].Id, c.OwnerEmployeeId); // escalation never transfers ownership
        Assert.Contains("Escalated", h.PopUpTitlesFor(Agent1));

        // Still neglected one business day later: the next level (the director) is told.
        await h.RunAsync(Day(2, 10, 0));
        Assert.Equal(2, h.ManagementEmail.Sent.Count);
        Assert.Equal(h.Director.Email, h.ManagementEmail.Sent[1].To.Single().Address);
        Assert.Equal(2, await h.Db.EscalationEvents.CountAsync(e => e.Outcome == EscalationOutcome.Executed && e.DeliveryResult == "Sent"));
    }

    // ---------------------------------------------------------------- Scenario 5

    [Fact]
    public async Task S5_AgentAcknowledgesOnly_InquiryStaysOpenAndSupervised()
    {
        using var h = new InquiryHarness(Agent1);
        var first = h.Receive(Agent1, Customer, "Heater problem", "I am experiencing a problem with your heater. Can you help me? It will not start.", Monday0900);
        await h.RunAsync(Monday0900.AddMinutes(1));

        h.Send(Agent1, Customer, "Re: Heater problem", "We have received your concern and will look into it.", Day(0, 10), inReplyTo: first);
        await h.RunAsync(Day(0, 10, 5));

        var c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(InquiryStage.InitialResponseReceived, c.Stage);
        Assert.Equal(CaseReplyStatus.Replied, c.ReplyStatus);
        Assert.NotEqual(CaseWorkStatus.Completed, c.WorkStatus);
        Assert.Null(c.CompletedAt);
        Assert.Equal(ResolutionVerdict.AcknowledgedOnly, c.ResolutionVerdict);

        // The stored agent reply is encrypted at rest, and only the agent's text is kept.
        var outbound = await h.Db.EmailMessages.SingleAsync(m => m.Direction == MessageDirection.Outbound);
        Assert.Null(outbound.BodyText);
        Assert.NotNull(outbound.BodyTextEncrypted);

        // The initial-response reminder no longer applies...
        await h.RunAsync(Day(0, 15, 0));
        Assert.Equal(0, await h.Db.Reminders.CountAsync());

        // ...but supervision did not end: after 3 calendar days of customer silence the agent is told.
        await h.RunAsync(Day(3, 10, 5));
        c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(InquiryStage.Inactive, c.Stage);
        Assert.Equal("Follow-up may be needed", h.PopUpTitlesFor(Agent1).Last());
    }

    // ---------------------------------------------------------------- Scenario 6

    [Fact]
    public async Task S6_CustomerFollowUp_JoinsTheSameInquiry_AndHandsTheNextStepBackToTheAgent()
    {
        using var h = new InquiryHarness(Agent1);
        var first = h.Receive(Agent1, Customer, "Heater problem", "I am experiencing a problem with your heater. Can you help me?", Monday0900);
        await h.RunAsync(Monday0900.AddMinutes(1));
        var reply = h.Send(Agent1, Customer, "Re: Heater problem", "Please try resetting the breaker and tell me if it starts.", Day(0, 10), inReplyTo: first);
        await h.RunAsync(Day(0, 10, 5));
        var aiCallsBefore = h.Ai.CallsByModel.Count;

        // A short follow-up with no inquiry wording at all - it must still join the conversation.
        h.Receive(Agent1, Customer, "Re: Heater problem", "Tried it. Still nothing.", Day(0, 11), inReplyTo: reply);
        await h.RunAsync(Day(0, 11, 5));

        Assert.Equal(1, await h.Db.Cases.CountAsync());
        var c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(InquiryStage.CustomerFollowUpReceived, c.Stage);
        Assert.Equal(CaseReplyStatus.AwaitingReply, c.ReplyStatus);
        Assert.Equal(3, await h.Db.CaseEmails.CountAsync(ce => ce.CaseId == c.Id)); // inbound, outbound, inbound
        Assert.Equal(aiCallsBefore, h.Ai.CallsByModel.Count);                       // linked by thread, not re-screened
        Assert.Equal(2, h.PopUpTitlesFor(Agent1).Count(t => t == "New Email"));      // new inquiry + follow-up

        // The follow-up gets its own timer: 1 business day, then reminders.
        await h.RunAsync(Day(1, 11, 0));
        await h.RunAsync(Day(1, 13, 0));
        Assert.Equal(1, await h.Db.Reminders.CountAsync(r => r.Trigger == ReminderTrigger.ResponseOverdue));
    }

    [Fact]
    public async Task S6b_FollowUpProcessedBeforeTheAgentReplyIsSynced_AgentStillOwesTheResponse()
    {
        using var h = new InquiryHarness(Agent1);
        var first = h.Receive(Agent1, Customer, "Heater problem", "My heater does not work. Can you help me?", Monday0900);
        await h.RunAsync(Monday0900.AddMinutes(1));

        // The reply (10:00) and the customer's answer to it (10:30) are both new when the next cycle runs;
        // intake processes the answer before the Sent-folder sync stores the reply.
        var reply = h.Send(Agent1, Customer, "Re: Heater problem", "Please reset the breaker.", Day(0, 10), inReplyTo: first);
        h.Receive(Agent1, Customer, "Re: Heater problem", "Did that, still broken.", Day(0, 10, 30), inReplyTo: reply);
        await h.RunAsync(Day(0, 10, 35));

        var c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(InquiryStage.CustomerFollowUpReceived, c.Stage);
        Assert.Equal(Day(0, 10, 30), c.StageChangedAt);
        Assert.Equal(Day(0, 10), c.FirstAgentResponseAt);
        Assert.Equal(1, await h.Db.Cases.CountAsync());
    }

    // ---------------------------------------------------------------- Scenario 7

    [Fact]
    public async Task S7_ConversationGoesInactive_AgentNotified_ThenUpdateRequested_ThenEscalatedIfIgnored()
    {
        using var h = new InquiryHarness(Agent1);
        var first = h.Receive(Agent1, Customer, "Heater problem", "My heater does not work. Can you help me?", Monday0900);
        await h.RunAsync(Monday0900.AddMinutes(1));
        h.Send(Agent1, Customer, "Re: Heater problem", "Could you send a photo of the control panel?", Day(0, 10), inReplyTo: first);
        await h.RunAsync(Day(0, 10, 5));

        // Customer silent - not the agent's fault, nothing for 3 calendar days.
        await h.RunAsync(Day(2, 16, 0));
        Assert.Equal(0, await h.Db.Reminders.CountAsync());
        Assert.Empty(h.ManagementEmail.Sent);

        // Thursday 10:05: waiting period over → agent told a follow-up may be needed; inquiry is Inactive.
        await h.RunAsync(Day(3, 10, 5));
        var c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(InquiryStage.Inactive, c.Stage);
        Assert.Equal(1, await h.Db.Reminders.CountAsync(r => r.Trigger == ReminderTrigger.CustomerSilent));

        // The agent ignores it for a business day: update requested, then reminders, then escalation.
        await h.RunAsync(Day(4, 10, 5));  // Fri: idle deadline (note)
        await h.RunAsync(Day(4, 12, 5));  // reminder 1
        await h.RunAsync(Day(4, 14, 5));  // reminder 2
        Assert.Equal(2, await h.Db.Reminders.CountAsync(r => r.Trigger == ReminderTrigger.ConversationIdle));
        Assert.Contains("Update needed", h.PopUpTitlesFor(Agent1));
        await h.RunAsync(Day(4, 16, 5));  // escalation
        var email = Assert.Single(h.ManagementEmail.Sent);
        Assert.Contains("Agent has replied:  Yes", email.TextBody);
        Assert.Contains("no progress", email.TextBody);
    }

    [Fact]
    public async Task S7b_AgentFollowsUpAfterSilenceNotice_TimersRestart_NoEscalation()
    {
        using var h = new InquiryHarness(Agent1);
        var first = h.Receive(Agent1, Customer, "Heater problem", "My heater does not work. Can you help me?", Monday0900);
        await h.RunAsync(Monday0900.AddMinutes(1));
        var reply = h.Send(Agent1, Customer, "Re: Heater problem", "Could you send a photo?", Day(0, 10), inReplyTo: first);
        await h.RunAsync(Day(0, 10, 5));
        await h.RunAsync(Day(3, 10, 5)); // silence notice → Inactive

        h.Send(Agent1, Customer, "Re: Heater problem", "Just checking - did you manage to take the photo?", Day(3, 11), inReplyTo: reply);
        await h.RunAsync(Day(3, 11, 5));
        var c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(InquiryStage.AgentFollowUpSent, c.Stage);

        await h.RunAsync(Day(4, 17, 0));
        Assert.Empty(h.ManagementEmail.Sent);
    }

    // ---------------------------------------------------------------- Scenario 9

    [Fact]
    public async Task S9_Resolution_AiProposes_AgentConfirms_RemindersAndEscalationStop()
    {
        using var h = new InquiryHarness(Agent1);
        var first = h.Receive(Agent1, Customer, "Heater problem", "My heater does not work. Can you help me?", Monday0900);
        await h.RunAsync(Monday0900.AddMinutes(1));
        var fix = h.Send(Agent1, Customer, "Re: Heater problem", "Please reset the breaker; that fixes this model.", Day(0, 10), inReplyTo: first);
        await h.RunAsync(Day(0, 10, 5));
        h.Receive(Agent1, Customer, "Re: Heater problem", "Thank you, it works now!", Day(0, 11), inReplyTo: fix);
        await h.RunAsync(Day(0, 11, 5));

        // The AI proposes - it never closes.
        var c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(InquiryStage.PotentiallyResolved, c.Stage);
        Assert.Equal(CaseWorkStatus.PotentiallyResolved, c.WorkStatus);
        Assert.Null(c.CompletedAt);
        Assert.Contains("Please confirm: resolved?", h.PopUpTitlesFor(Agent1));

        // Days pass with nobody confirming: the agent is asked to review - no escalation, no auto-close.
        await h.RunAsync(Day(3, 11, 10));
        await h.RunAsync(Day(9, 12, 0));
        c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(InquiryStage.PotentiallyResolved, c.Stage);
        Assert.Empty(h.ManagementEmail.Sent);
        Assert.Contains("Follow-up may be needed", h.PopUpTitlesFor(Agent1));

        // The agent confirms (Windows Agent action) → Resolved; supervision ends.
        var result = await h.AgentActions.SubmitActionAsync(Guid.NewGuid(), h.Agents[Agent1].Id,
            new SubmitCaseActionRequest("confirm-1", c.Id, CaseActionType.ConfirmResolved, "Customer confirmed", Day(9, 12, 5)), CancellationToken.None);
        Assert.True(result.Succeeded, result.Error);
        c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(InquiryStage.Resolved, c.Stage);
        Assert.Equal(CaseWorkStatus.Completed, c.WorkStatus);
        Assert.Equal(CaseCompletionReason.CustomerRequestResolved, c.CompletionReason);

        var remindersAtClose = await h.Db.Reminders.CountAsync();
        var popUpsAtClose = h.PopUps.Sent.Count;
        await h.RunAsync(Day(20, 12, 0));
        await h.RunAsync(Day(40, 12, 0));
        Assert.Equal(remindersAtClose, await h.Db.Reminders.CountAsync());
        Assert.Equal(popUpsAtClose, h.PopUps.Sent.Count);
        Assert.Empty(h.ManagementEmail.Sent);
    }

    [Fact]
    public async Task S9b_AgentRejectsAiSuggestion_SupervisionContinues()
    {
        using var h = new InquiryHarness(Agent1);
        var first = h.Receive(Agent1, Customer, "Heater problem", "My heater does not work. Can you help me?", Monday0900);
        await h.RunAsync(Monday0900.AddMinutes(1));
        var fix = h.Send(Agent1, Customer, "Re: Heater problem", "Please reset the breaker.", Day(0, 10), inReplyTo: first);
        await h.RunAsync(Day(0, 10, 5));
        h.Receive(Agent1, Customer, "Re: Heater problem", "Thank you, it works now!", Day(0, 11), inReplyTo: fix);
        await h.RunAsync(Day(0, 11, 5));
        var c = await h.CaseAsync(Agent1, Customer);

        var result = await h.AgentActions.SubmitActionAsync(Guid.NewGuid(), h.Agents[Agent1].Id,
            new SubmitCaseActionRequest("reject-1", c.Id, CaseActionType.RejectResolution, "Still need to ship a spare part", Day(0, 12)), CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(InquiryStage.CustomerFollowUpReceived, c.Stage);
        Assert.NotEqual(CaseWorkStatus.Completed, c.WorkStatus);
    }

    [Fact]
    public async Task S9c_CustomerWritesAgainAfterResolution_InquiryReopens()
    {
        using var h = new InquiryHarness(Agent1);
        var first = h.Receive(Agent1, Customer, "Heater problem", "My heater does not work. Can you help me?", Monday0900);
        await h.RunAsync(Monday0900.AddMinutes(1));
        var fix = h.Send(Agent1, Customer, "Re: Heater problem", "Please reset the breaker.", Day(0, 10), inReplyTo: first);
        await h.RunAsync(Day(0, 10, 5));
        var c = await h.CaseAsync(Agent1, Customer);
        await h.AgentActions.SubmitActionAsync(Guid.NewGuid(), h.Agents[Agent1].Id,
            new SubmitCaseActionRequest("confirm-1", c.Id, CaseActionType.ConfirmResolved, null, Day(0, 10, 10)), CancellationToken.None);

        h.Receive(Agent1, Customer, "Re: Heater problem", "It stopped again this morning.", Day(2, 9), inReplyTo: fix);
        await h.RunAsync(Day(2, 9, 5));

        c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(InquiryStage.CustomerFollowUpReceived, c.Stage);
        Assert.Equal(1, c.ReopenCount);
        Assert.Equal(1, await h.Db.Cases.CountAsync());
    }

    // ---------------------------------------------------------------- Scenario 10

    [Fact]
    public async Task S10_MultipleAgents_EachInquiryTrackedIndependently_OwnershipNeverCrosses()
    {
        using var h = new InquiryHarness(Agent1, Agent2, Agent3);
        var a1 = h.Receive(Agent1, Customer, "Request for quotation", "Please request a quotation for 5 units. What is the price?", Monday0900);
        var a2 = h.Receive(Agent2, Customer, "Request for quotation", "Please request a quotation for 5 units. What is the price?", Monday0900);
        h.Receive(Agent3, "bo@other.example", "Pricing", "Could you send your price list? How much is delivery?", Monday0900);
        await h.RunAsync(Monday0900.AddMinutes(1));

        Assert.Equal(3, await h.Db.Cases.CountAsync());
        foreach (var mailbox in new[] { Agent1, Agent2 })
        {
            Assert.Equal(h.Agents[mailbox].Id, (await h.CaseAsync(mailbox, Customer)).OwnerEmployeeId);
        }
        Assert.Equal(h.Agents[Agent3].Id, (await h.CaseAsync(Agent3, "bo@other.example")).OwnerEmployeeId);

        // Only agent 2 replies; that reply belongs to agent 2's inquiry alone.
        h.Send(Agent2, Customer, "Re: Request for quotation", "Here is your quotation: 5 units at 400 EUR each.", Day(0, 10), inReplyTo: a2);
        await h.RunAsync(Day(0, 10, 5));
        Assert.Equal(InquiryStage.InitialResponseReceived, (await h.CaseAsync(Agent2, Customer)).Stage);
        Assert.Equal(InquiryStage.AwaitingInitialResponse, (await h.CaseAsync(Agent1, Customer)).Stage);
        Assert.Equal(InquiryStage.AwaitingInitialResponse, (await h.CaseAsync(Agent3, "bo@other.example")).Stage);

        // Reminders reach only the agents who still owe a reply.
        await h.RunAsync(Day(0, 13, 0));
        await h.RunAsync(Day(0, 15, 0));
        Assert.Contains("Reminder", h.PopUpTitlesFor(Agent1));
        Assert.Contains("Reminder", h.PopUpTitlesFor(Agent3));
        Assert.DoesNotContain("Reminder", h.PopUpTitlesFor(Agent2));

        // Escalations name the right agent.
        await h.RunAsync(Day(0, 17, 0));
        await h.RunAsync(Day(1, 10, 0));
        Assert.Equal(2, h.ManagementEmail.Sent.Count);
        Assert.Contains(h.ManagementEmail.Sent, e => e.TextBody.Contains("<agent1@sawo.com>"));
        Assert.Contains(h.ManagementEmail.Sent, e => e.TextBody.Contains("<agent3@sawo.com>"));
        Assert.DoesNotContain(h.ManagementEmail.Sent, e => e.TextBody.Contains("<agent2@sawo.com>"));
        _ = a1;
    }

    // ---------------------------------------------------------------- Q3: internal email

    [Fact]
    public async Task Q3_InternalForwardAndColleagueReply_StayLinked_WithoutChangingWhoOwesTheNextStep()
    {
        using var h = new InquiryHarness(Agent1);
        // Even with sawo.com on the Ignored senders list, in-thread internal mail must stay linked.
        h.Db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.IgnoredSenders, Value = "sawo.com" });
        await h.Db.SaveChangesAsync();

        var first = h.Receive(Agent1, Customer, "Warranty question", "Request a quotation for a warranty extension please. How much?", Monday0900);
        await h.RunAsync(Monday0900.AddMinutes(1));

        var forward = h.Send(Agent1, "service@sawo.com", "Fwd: Warranty question", "Can service check warranty options?", Day(0, 9, 30), inReplyTo: first);
        await h.RunAsync(Day(0, 9, 35));
        var c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(InquiryStage.AwaitingInitialResponse, c.Stage); // a forward is not a reply to the customer
        Assert.Null(c.FirstAgentResponseAt);

        h.Receive(Agent1, "service@sawo.com", "Re: Fwd: Warranty question", "Option B applies, 3 extra years.", Day(0, 10), inReplyTo: forward);
        await h.RunAsync(Day(0, 10, 5));

        Assert.Equal(1, await h.Db.Cases.CountAsync());
        c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(InquiryStage.AwaitingInitialResponse, c.Stage);
        Assert.NotNull(c.LastInternalActivityAt);
        Assert.Equal(2, await h.Db.CaseEmails.CountAsync(ce => ce.CaseId == c.Id && ce.Role == ConversationRole.Internal));

        // The deadline still applies - internal activity did not answer the customer.
        await h.RunAsync(Day(0, 13, 0));
        await h.RunAsync(Day(0, 15, 0));
        Assert.Contains("Reminder", h.PopUpTitlesFor(Agent1));
    }

    [Fact]
    public async Task WaitingForInternal_AskedForUpdateAfterABusinessDay()
    {
        using var h = new InquiryHarness(Agent1);
        var first = h.Receive(Agent1, Customer, "Custom order", "Can you provide a quotation for a custom sauna heater? How much?", Monday0900);
        await h.RunAsync(Monday0900.AddMinutes(1));
        h.Send(Agent1, Customer, "Re: Custom order", "I am checking with production and will come back to you.", Day(0, 10), inReplyTo: first);
        await h.RunAsync(Day(0, 10, 5));
        var c = await h.CaseAsync(Agent1, Customer);
        await h.AgentActions.SubmitActionAsync(Guid.NewGuid(), h.Agents[Agent1].Id,
            new SubmitCaseActionRequest("wait-1", c.Id, CaseActionType.WaitingForInternal, null, Day(0, 10, 10)), CancellationToken.None);
        c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(InquiryStage.AwaitingInternal, c.Stage);

        await h.RunAsync(Day(1, 9, 0));
        Assert.Equal(InquiryStage.AwaitingInternal, (await h.CaseAsync(Agent1, Customer)).Stage);

        await h.RunAsync(Day(1, 11, 0)); // 1 business day after the agent set the wait
        Assert.Contains("Update needed", h.PopUpTitlesFor(Agent1));
        Assert.Equal(InquiryStage.Inactive, (await h.CaseAsync(Agent1, Customer)).Stage);
    }

    // ---------------------------------------------------------------- Overdue target

    [Fact]
    public async Task ResolutionTarget_MarksOverdue_ButDoesNotEscalateWhileTheCustomerOwesTheReply()
    {
        using var h = new InquiryHarness(Agent1);
        var first = h.Receive(Agent1, Customer, "Project quote", "Can you provide a quotation for our hotel project? How much?", Monday0900);
        await h.RunAsync(Monday0900.AddMinutes(1));
        var last = h.Send(Agent1, Customer, "Re: Project quote", "Which room sizes do you have?", Day(0, 10), inReplyTo: first);
        await h.RunAsync(Day(0, 10, 5));

        // Ping-pong keeps it active but unresolved past the 5-business-day target.
        for (var day = 1; day <= 8; day++) // through Tuesday of the following week; the target is next Monday 09:00
        {
            var q = h.Receive(Agent1, Customer, "Re: Project quote", $"Answer {day}. One more question?", Day(day, 9), inReplyTo: last);
            await h.RunAsync(Day(day, 9, 5));
            last = h.Send(Agent1, Customer, "Re: Project quote", $"Thanks, noted {day}. Next question?", Day(day, 9, 30), inReplyTo: q);
            await h.RunAsync(Day(day, 9, 35));
        }

        var c = await h.CaseAsync(Agent1, Customer);
        Assert.NotNull(c.OverdueSince);
        Assert.Contains("Overdue", h.PopUpTitlesFor(Agent1));
        Assert.Equal(InquiryStage.AgentFollowUpSent, c.Stage);
        Assert.Empty(h.ManagementEmail.Sent); // the customer owes the next reply
    }
}
