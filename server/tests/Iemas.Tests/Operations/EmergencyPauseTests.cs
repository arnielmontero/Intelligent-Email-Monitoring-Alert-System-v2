using Iemas.Application.Cases;
using Iemas.Application.Escalations;
using Iemas.Application.Notifications;
using Iemas.Application.Operations;
using Iemas.Application.Reminders;
using Iemas.Domain.Cases;
using Iemas.Domain.Escalations;
using Iemas.Domain.Identity;
using Iemas.Domain.Notifications;
using Iemas.Domain.Operations;
using Iemas.Domain.Reminders;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Tests.Operations;

public class EmergencyPauseTests
{
    private static EmergencyPauseService CreateService(TestDbContext db, NoOpAuditService audit) =>
        new(db, new FakeCurrentUserService(email: "ops@sawo.com", roles: SystemRole.Administrator), audit);

    private static Case CreateCase(Guid? ownerEmployeeId = null) => new()
    {
        CaseNumber = $"CASE-{Guid.NewGuid():N}"[..12],
        EmailAccountId = Guid.NewGuid(),
        CustomerEmailAddress = "customer@example.com",
        Subject = "Price Request",
        NormalizedSubject = "Price Request",
        OwnerEmployeeId = ownerEmployeeId,
        WorkStatus = CaseWorkStatus.ActionRequired,
        ReplyStatus = CaseReplyStatus.AwaitingReply,
        FirstEmailReceivedAt = DateTimeOffset.UtcNow.AddDays(-2),
        LastActivityAt = DateTimeOffset.UtcNow,
    };

    private static async Task PauseAsync(TestDbContext db, PauseControl control)
    {
        db.EmergencyPauseControls.Add(new EmergencyPauseControl { Control = control, IsPaused = true, Reason = "test" });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetAllAsync_NoRows_ReturnsAllFiveControlsUnpaused()
    {
        using var db = TestDbContext.CreateNew();
        var controls = await CreateService(db, new NoOpAuditService()).GetAllAsync(CancellationToken.None);

        Assert.Equal(5, controls.Count);
        Assert.All(controls, c => Assert.False(c.IsPaused));
    }

    [Fact]
    public async Task SetAsync_PauseWithoutReason_IsRejected()
    {
        using var db = TestDbContext.CreateNew();
        var result = await CreateService(db, new NoOpAuditService()).SetAsync("Reminders", new SetPauseRequest(true, "  "), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(await db.EmergencyPauseControls.AnyAsync());
    }

    /// <summary>§91 — every change is audited, timestamped and attributed to a user.</summary>
    [Fact]
    public async Task SetAsync_PauseThenResume_RecordsAttributionAndAuditsBoth()
    {
        using var db = TestDbContext.CreateNew();
        var audit = new NoOpAuditService();
        var service = CreateService(db, audit);

        var paused = await service.SetAsync("escalations", new SetPauseRequest(true, "Policy review"), CancellationToken.None);
        Assert.True(paused.Succeeded);
        Assert.True(paused.Value!.IsPaused);
        Assert.Equal("ops@sawo.com", paused.Value.ChangedByEmail);
        Assert.NotNull(paused.Value.ChangedAt);

        var resumed = await service.SetAsync("Escalations", new SetPauseRequest(false, null), CancellationToken.None);
        Assert.False(resumed.Value!.IsPaused);

        Assert.Equal(new[] { "EMERGENCY_PAUSE_ENABLED", "EMERGENCY_PAUSE_DISABLED" }, audit.Entries.Select(e => e.Action));
        Assert.Equal("Policy review", audit.Entries[0].Details);
        Assert.Single(await db.EmergencyPauseControls.ToListAsync());
    }

    [Fact]
    public async Task SetAsync_UnknownControl_IsRejected()
    {
        using var db = TestDbContext.CreateNew();
        var result = await CreateService(db, new NoOpAuditService()).SetAsync("Everything", new SetPauseRequest(true, "x"), CancellationToken.None);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ReminderEngine_RemindersPaused_LeavesDueReminderScheduled()
    {
        using var db = TestDbContext.CreateNew();
        var targetCase = CreateCase(Guid.NewGuid());
        db.Cases.Add(targetCase);
        var reminder = new Reminder
        {
            CaseId = targetCase.Id, Trigger = ReminderTrigger.InitialActionRequired, Status = ReminderStatus.Scheduled,
            SequenceNumber = 1, ScheduledForUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
        };
        db.Reminders.Add(reminder);
        await PauseAsync(db, PauseControl.Reminders);

        var dispatcher = new FakeAgentNotificationDispatcher();
        var result = await new ReminderExecutionService(db, new ReminderSchedulingService(db), new NotificationService(db, dispatcher))
            .RunAsync(10, CancellationToken.None);

        Assert.Equal(0, result.Considered);
        Assert.Equal(ReminderStatus.Scheduled, (await db.Reminders.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(dispatcher.Sent);
    }

    /// <summary>A reminder the employee can't see must not count toward an escalation threshold.</summary>
    [Fact]
    public async Task ReminderEngine_AgentNotificationsPaused_AlsoHolds()
    {
        using var db = TestDbContext.CreateNew();
        var targetCase = CreateCase(Guid.NewGuid());
        db.Cases.Add(targetCase);
        db.Reminders.Add(new Reminder
        {
            CaseId = targetCase.Id, Trigger = ReminderTrigger.InitialActionRequired, Status = ReminderStatus.Scheduled,
            SequenceNumber = 1, ScheduledForUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
        });
        await PauseAsync(db, PauseControl.AgentNotifications);

        var result = await new ReminderExecutionService(db, new ReminderSchedulingService(db)).RunAsync(10, CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Equal(ReminderStatus.Scheduled, (await db.Reminders.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task EscalationEngine_Paused_ExecutesNothing()
    {
        using var db = TestDbContext.CreateNew();
        var owner = new Employee { FullName = "John Smith", Email = "john@sawo.com" };
        db.Employees.Add(owner);
        var policy = new EscalationPolicy
        {
            Name = "Default", Enabled = true, IsDefault = true, TriggerReminderCount = 1,
            GracePeriod = TimeSpan.Zero, Cooldown = TimeSpan.Zero, MaximumLevel = 1, Channel = "Email",
        };
        db.EscalationPolicies.Add(policy);
        db.EscalationLevels.Add(new EscalationLevel { EscalationPolicyId = policy.Id, Level = 1, RecipientType = EscalationRecipientType.Employee });
        var targetCase = CreateCase(owner.Id);
        db.Cases.Add(targetCase);
        db.Reminders.Add(new Reminder
        {
            CaseId = targetCase.Id, Trigger = ReminderTrigger.InitialActionRequired, Status = ReminderStatus.Sent,
            SequenceNumber = 1, ScheduledForUtc = DateTimeOffset.UtcNow.AddHours(-1),
        });
        await PauseAsync(db, PauseControl.Escalations);

        var result = await new EscalationService(db).RunAsync(10, CancellationToken.None);

        Assert.Equal(0, result.Executed);
        Assert.False(await db.EscalationEvents.AnyAsync());
        Assert.Equal(CaseWorkStatus.ActionRequired, (await db.Cases.AsNoTracking().SingleAsync()).WorkStatus);
    }

    [Fact]
    public async Task CaseWorkflow_EmailProcessingPaused_CreatesNoCases()
    {
        using var db = TestDbContext.CreateNew();
        await PauseAsync(db, PauseControl.EmailProcessing);

        var result = await new CaseWorkflowService(db, new CaseMatchingService(db), new ReminderSchedulingService(db))
            .RunAsync(10, CancellationToken.None);

        Assert.Equal(0, result.ConsideredCount);
    }

    /// <summary>§91 "Pausing must not delete Cases" — paused notifications are recorded as Cancelled, never pushed, and the Case is untouched.</summary>
    [Fact]
    public async Task NotificationService_AgentNotificationsPaused_RecordsCancelledAndDoesNotPush()
    {
        using var db = TestDbContext.CreateNew();
        var owner = new Employee { FullName = "John Smith", Email = "john@sawo.com" };
        db.Employees.Add(owner);
        var targetCase = CreateCase(owner.Id);
        db.Cases.Add(targetCase);
        await PauseAsync(db, PauseControl.AgentNotifications);

        var dispatcher = new FakeAgentNotificationDispatcher();
        var outcome = await new NotificationService(db, dispatcher).SendForCaseAsync(
            NotificationType.NewEmail, targetCase, Application.Common.Interfaces.AgentPushCommandType.ShowCase, null, CancellationToken.None);

        Assert.Equal(NotificationSendOutcome.Suppressed, outcome);
        Assert.Empty(dispatcher.Sent);
        var notification = await db.Notifications.SingleAsync();
        Assert.Equal(NotificationDeliveryStatus.Cancelled, notification.Status);
        Assert.Contains("paused", notification.FailureReason);
        Assert.Single(await db.Cases.ToListAsync());
    }
}
