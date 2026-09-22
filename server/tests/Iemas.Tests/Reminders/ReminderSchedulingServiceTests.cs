using Iemas.Application.Reminders;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Iemas.Domain.Identity;
using Iemas.Domain.Reminders;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iemas.Tests.Reminders;

public class ReminderSchedulingServiceTests
{
    private static ReminderPolicy CreateDefaultPolicy(bool enabled = true, int maxReminders = 3)
    {
        return new ReminderPolicy
        {
            Name = "Default",
            Enabled = enabled,
            IsDefault = true,
            InitialDelay = TimeSpan.FromHours(4),
            ReminderInterval = TimeSpan.FromHours(24),
            MaxReminders = maxReminders,
            MinimumInterval = TimeSpan.FromHours(1),
            RestrictToBusinessHours = false,
            ExcludeWeekends = false,
            TimeZoneId = "UTC",
        };
    }

    private static Case CreateCase(CaseWorkStatus workStatus = CaseWorkStatus.ActionRequired, CaseReplyStatus replyStatus = CaseReplyStatus.AwaitingReply)
    {
        return new Case
        {
            CaseNumber = $"CASE-{Guid.NewGuid():N}"[..12],
            EmailAccountId = Guid.NewGuid(),
            CustomerEmailAddress = "customer@example.com",
            Subject = "Price Request",
            NormalizedSubject = "Price Request",
            WorkStatus = workStatus,
            ReplyStatus = replyStatus,
            FirstEmailReceivedAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>§54 "Initial Notification" — a fresh ActionRequired Case gets its first reminder scheduled after the policy's initial delay.</summary>
    [Fact]
    public async Task ScheduleInitialReminderAsync_EligibleCase_CreatesSequenceOneReminder()
    {
        using var db = TestDbContext.CreateNew();
        db.ReminderPolicies.Add(CreateDefaultPolicy());
        var targetCase = CreateCase();
        db.Cases.Add(targetCase);
        await db.SaveChangesAsync();

        var service = new ReminderSchedulingService(db);
        var reminder = await service.ScheduleInitialReminderAsync(targetCase.Id, CancellationToken.None);

        Assert.NotNull(reminder);
        Assert.Equal(1, reminder!.SequenceNumber);
        Assert.Equal(ReminderTrigger.InitialActionRequired, reminder.Trigger);
        Assert.Equal(ReminderStatus.Scheduled, reminder.Status);
        Assert.True(reminder.ScheduledForUtc > DateTimeOffset.UtcNow);
    }

    /// <summary>Idempotency: calling ScheduleInitialReminderAsync twice for the same still-Scheduled Case must not create a second reminder.</summary>
    [Fact]
    public async Task ScheduleInitialReminderAsync_CalledTwice_DoesNotDuplicate()
    {
        using var db = TestDbContext.CreateNew();
        db.ReminderPolicies.Add(CreateDefaultPolicy());
        var targetCase = CreateCase();
        db.Cases.Add(targetCase);
        await db.SaveChangesAsync();

        var service = new ReminderSchedulingService(db);
        await service.ScheduleInitialReminderAsync(targetCase.Id, CancellationToken.None);
        var second = await service.ScheduleInitialReminderAsync(targetCase.Id, CancellationToken.None);

        Assert.Null(second);
        Assert.Equal(1, await db.Reminders.CountAsync(r => r.CaseId == targetCase.Id));
    }

    /// <summary>A Case that is already Completed must never get a reminder scheduled.</summary>
    [Fact]
    public async Task ScheduleInitialReminderAsync_CompletedCase_ReturnsNull()
    {
        using var db = TestDbContext.CreateNew();
        db.ReminderPolicies.Add(CreateDefaultPolicy());
        var targetCase = CreateCase(workStatus: CaseWorkStatus.Completed);
        db.Cases.Add(targetCase);
        await db.SaveChangesAsync();

        var service = new ReminderSchedulingService(db);
        var reminder = await service.ScheduleInitialReminderAsync(targetCase.Id, CancellationToken.None);

        Assert.Null(reminder);
    }

    /// <summary>A Case whose reply is already verified must never get a reminder scheduled.</summary>
    [Fact]
    public async Task ScheduleInitialReminderAsync_AlreadyReplied_ReturnsNull()
    {
        using var db = TestDbContext.CreateNew();
        db.ReminderPolicies.Add(CreateDefaultPolicy());
        var targetCase = CreateCase(replyStatus: CaseReplyStatus.Replied);
        db.Cases.Add(targetCase);
        await db.SaveChangesAsync();

        var service = new ReminderSchedulingService(db);
        var reminder = await service.ScheduleInitialReminderAsync(targetCase.Id, CancellationToken.None);

        Assert.Null(reminder);
    }

    /// <summary>No enabled policy at all (none configured / applicable one disabled) — scheduling is a quiet no-op, not an error.</summary>
    [Fact]
    public async Task ScheduleInitialReminderAsync_NoEnabledPolicy_ReturnsNull()
    {
        using var db = TestDbContext.CreateNew();
        db.ReminderPolicies.Add(CreateDefaultPolicy(enabled: false));
        var targetCase = CreateCase();
        db.Cases.Add(targetCase);
        await db.SaveChangesAsync();

        var service = new ReminderSchedulingService(db);
        var reminder = await service.ScheduleInitialReminderAsync(targetCase.Id, CancellationToken.None);

        Assert.Null(reminder);
    }

    /// <summary>§54 "No infinite reminder loops" / "Maximum reminders" — a follow-up beyond MaxReminders is never scheduled.</summary>
    [Fact]
    public async Task ScheduleFollowUpReminderAsync_AtMaxReminders_ReturnsNull()
    {
        using var db = TestDbContext.CreateNew();
        var policy = CreateDefaultPolicy(maxReminders: 2);
        db.ReminderPolicies.Add(policy);
        var targetCase = CreateCase();
        db.Cases.Add(targetCase);
        await db.SaveChangesAsync();

        var sentReminder = new Reminder
        {
            CaseId = targetCase.Id,
            ReminderPolicyId = policy.Id,
            Trigger = ReminderTrigger.FollowUp,
            Status = ReminderStatus.Sent,
            SequenceNumber = 2, // already at the ceiling
            ScheduledForUtc = DateTimeOffset.UtcNow.AddHours(-1),
        };
        db.Reminders.Add(sentReminder);
        await db.SaveChangesAsync();

        var service = new ReminderSchedulingService(db);
        var followUp = await service.ScheduleFollowUpReminderAsync(sentReminder, CancellationToken.None);

        Assert.Null(followUp);
    }

    /// <summary>Below the ceiling, a follow-up reminder increments SequenceNumber and schedules ReminderInterval out.</summary>
    [Fact]
    public async Task ScheduleFollowUpReminderAsync_BelowMax_SchedulesNextSequence()
    {
        using var db = TestDbContext.CreateNew();
        var policy = CreateDefaultPolicy(maxReminders: 3);
        db.ReminderPolicies.Add(policy);
        var targetCase = CreateCase();
        db.Cases.Add(targetCase);
        await db.SaveChangesAsync();

        var sentReminder = new Reminder
        {
            CaseId = targetCase.Id,
            ReminderPolicyId = policy.Id,
            Trigger = ReminderTrigger.InitialActionRequired,
            Status = ReminderStatus.Sent,
            SequenceNumber = 1,
            ScheduledForUtc = DateTimeOffset.UtcNow.AddHours(-1),
        };
        db.Reminders.Add(sentReminder);
        await db.SaveChangesAsync();

        var service = new ReminderSchedulingService(db);
        var followUp = await service.ScheduleFollowUpReminderAsync(sentReminder, CancellationToken.None);

        Assert.NotNull(followUp);
        Assert.Equal(2, followUp!.SequenceNumber);
        Assert.Equal(ReminderTrigger.FollowUp, followUp.Trigger);
    }

    /// <summary>§56 "Remind me at 3:00 PM" — an employee-requested reminder is scheduled at (or after, per minimum interval) the requested time.</summary>
    [Fact]
    public async Task ScheduleEmployeeRequestedReminderAsync_ValidRequest_SchedulesAtRequestedTime()
    {
        using var db = TestDbContext.CreateNew();
        db.ReminderPolicies.Add(CreateDefaultPolicy());
        var targetCase = CreateCase();
        db.Cases.Add(targetCase);
        await db.SaveChangesAsync();

        var requestedFor = DateTimeOffset.UtcNow.AddHours(5);
        var service = new ReminderSchedulingService(db);
        var result = await service.ScheduleEmployeeRequestedReminderAsync(targetCase.Id, Guid.NewGuid(), requestedFor, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(requestedFor, result.Value!.ScheduledForUtc);
        Assert.Equal(ReminderTrigger.EmployeeRequested, result.Value.Trigger);
        Assert.Equal(requestedFor, result.Value.RequestedForUtc);
    }

    /// <summary>§78 idempotency — the same source AgentCaseAction must never produce two Reminder rows, even across two separate calls.</summary>
    [Fact]
    public async Task ScheduleEmployeeRequestedReminderAsync_SameSourceActionTwice_ReturnsSameReminder()
    {
        using var db = TestDbContext.CreateNew();
        db.ReminderPolicies.Add(CreateDefaultPolicy());
        var targetCase = CreateCase();
        db.Cases.Add(targetCase);
        await db.SaveChangesAsync();

        var sourceActionId = Guid.NewGuid();
        var service = new ReminderSchedulingService(db);
        var first = await service.ScheduleEmployeeRequestedReminderAsync(targetCase.Id, sourceActionId, DateTimeOffset.UtcNow.AddHours(3), CancellationToken.None);
        var second = await service.ScheduleEmployeeRequestedReminderAsync(targetCase.Id, sourceActionId, DateTimeOffset.UtcNow.AddHours(3), CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(first.Value!.Id, second.Value!.Id);
        Assert.Equal(1, await db.Reminders.CountAsync(r => r.SourceAgentCaseActionId == sourceActionId));
    }

    /// <summary>§54 "Minimum interval" applies to Remind Later too — a requested time sooner than MinimumInterval after the last reminder is pushed out to the floor, not honored verbatim.</summary>
    [Fact]
    public async Task ScheduleEmployeeRequestedReminderAsync_RequestedSoonerThanMinimumInterval_IsDeferredToFloor()
    {
        using var db = TestDbContext.CreateNew();
        var policy = CreateDefaultPolicy();
        policy.MinimumInterval = TimeSpan.FromHours(6);
        db.ReminderPolicies.Add(policy);
        var targetCase = CreateCase();
        db.Cases.Add(targetCase);
        await db.SaveChangesAsync();

        var lastReminderAt = DateTimeOffset.UtcNow;
        db.Reminders.Add(new Reminder
        {
            CaseId = targetCase.Id,
            ReminderPolicyId = policy.Id,
            Trigger = ReminderTrigger.InitialActionRequired,
            Status = ReminderStatus.Sent,
            SequenceNumber = 1,
            ScheduledForUtc = lastReminderAt,
        });
        await db.SaveChangesAsync();

        // Employee asks to be reminded in 1 hour, but the floor is 6 hours after the last reminder.
        var requestedFor = lastReminderAt.AddHours(1);
        var service = new ReminderSchedulingService(db);
        var result = await service.ScheduleEmployeeRequestedReminderAsync(targetCase.Id, Guid.NewGuid(), requestedFor, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.Value!.ScheduledForUtc >= lastReminderAt.Add(policy.MinimumInterval));
    }

    /// <summary>A RemindLater request against a Case that has already resolved must fail cleanly, not silently schedule a meaningless reminder.</summary>
    [Fact]
    public async Task ScheduleEmployeeRequestedReminderAsync_CaseAlreadyCompleted_Fails()
    {
        using var db = TestDbContext.CreateNew();
        db.ReminderPolicies.Add(CreateDefaultPolicy());
        var targetCase = CreateCase(workStatus: CaseWorkStatus.Completed);
        db.Cases.Add(targetCase);
        await db.SaveChangesAsync();

        var service = new ReminderSchedulingService(db);
        var result = await service.ScheduleEmployeeRequestedReminderAsync(targetCase.Id, Guid.NewGuid(), null, CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    /// <summary>§54 policy resolution — a policy scoped to the Case's Classification Profile takes precedence over the default policy.</summary>
    [Fact]
    public async Task ScheduleInitialReminderAsync_ProfileScopedPolicy_TakesPrecedenceOverDefault()
    {
        using var db = TestDbContext.CreateNew();
        var profileId = Guid.NewGuid();

        var defaultPolicy = CreateDefaultPolicy();
        defaultPolicy.InitialDelay = TimeSpan.FromHours(4);
        db.ReminderPolicies.Add(defaultPolicy);

        var scopedPolicy = new ReminderPolicy
        {
            Name = "VIP",
            Enabled = true,
            IsDefault = false,
            ClassificationProfileId = profileId,
            InitialDelay = TimeSpan.FromMinutes(15),
            ReminderInterval = TimeSpan.FromHours(2),
            MaxReminders = 5,
            MinimumInterval = TimeSpan.FromMinutes(30),
            RestrictToBusinessHours = false,
            ExcludeWeekends = false,
            TimeZoneId = "UTC",
        };
        db.ReminderPolicies.Add(scopedPolicy);

        var account = new EmailAccount
        {
            EmailAddress = "vip@sawo.com", Purpose = EmailAccountPurpose.Inbound, Protocol = EmailProtocol.Imap,
            Host = "imap.example.com", Port = 993, Username = "vip@sawo.com", AuthMethod = EmailAuthMethod.Password,
        };
        db.EmailAccounts.Add(account);

        var targetCase = CreateCase();
        targetCase.EmailAccountId = account.Id;
        db.Cases.Add(targetCase);

        var message = new EmailMessage
        {
            EmailAccountId = account.Id, Provider = EmailProtocol.Imap, ProviderMessageId = Guid.NewGuid().ToString(),
            FromAddress = "customer@example.com", ToAddresses = "vip@sawo.com", Subject = "Price Request",
            ReceivedAt = DateTimeOffset.UtcNow, ProcessingStatus = EmailProcessingStatus.Processed, CaseId = targetCase.Id,
        };
        db.EmailMessages.Add(message);
        db.CaseEmails.Add(new CaseEmail { CaseId = targetCase.Id, EmailMessageId = message.Id, MatchSignal = CaseMatchSignal.NewCase });
        db.EmailClassifications.Add(new Iemas.Domain.Ai.EmailClassification
        {
            EmailMessageId = message.Id, Decision = Iemas.Domain.Ai.ImportanceDecision.Important, ClassificationProfileId = profileId,
        });
        await db.SaveChangesAsync();

        var service = new ReminderSchedulingService(db);
        var reminder = await service.ScheduleInitialReminderAsync(targetCase.Id, CancellationToken.None);

        Assert.NotNull(reminder);
        Assert.Equal(scopedPolicy.Id, reminder!.ReminderPolicyId);
        // Scoped policy's 15-minute delay, not the default's 4 hours.
        Assert.True(reminder.ScheduledForUtc < DateTimeOffset.UtcNow.AddHours(1));
    }
}
