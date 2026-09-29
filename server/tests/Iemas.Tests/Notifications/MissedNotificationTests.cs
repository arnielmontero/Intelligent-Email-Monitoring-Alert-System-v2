using Iemas.Api.Realtime;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Notifications;
using Iemas.Domain.Cases;
using Iemas.Domain.Identity;
using Iemas.Domain.Notifications;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iemas.Tests.Notifications;

/// <summary>Pop-ups raised while the employee's Agent was offline are shown when it connects, instead of being lost.</summary>
public class MissedNotificationTests
{
    private static Case CreateCase(Guid ownerId, CaseWorkStatus status = CaseWorkStatus.ActionRequired, string number = "CASE-000001") => new()
    {
        CaseNumber = number,
        EmailAccountId = Guid.NewGuid(),
        CustomerEmailAddress = "maria@customer.example",
        Subject = "Quotation for 20 units",
        NormalizedSubject = "Quotation for 20 units",
        OwnerEmployeeId = ownerId,
        WorkStatus = status,
        ReplyStatus = CaseReplyStatus.AwaitingReply,
        FirstEmailReceivedAt = DateTimeOffset.UtcNow.AddHours(-1),
        LastActivityAt = DateTimeOffset.UtcNow,
    };

    private static Notification Queued(Guid employeeId, Case theCase, NotificationType type, DateTimeOffset createdAt) => new()
    {
        EmployeeId = employeeId, CaseId = theCase.Id, Type = type, Title = $"{type}", Message = theCase.Subject,
        Status = NotificationDeliveryStatus.Queued, CreatedAt = createdAt,
    };

    [Fact]
    public async Task DeliverMissed_SendsQueuedPopUpsForOpenCasesOnce_SkipsFinishedAndOld()
    {
        using var db = TestDbContext.CreateNew();
        var owner = new Employee { FullName = "Arniel Montero", Email = "arniel.montero@sawo.com" };
        db.Employees.Add(owner);
        var open = CreateCase(owner.Id);
        var done = CreateCase(owner.Id, CaseWorkStatus.Completed, "CASE-000002");
        db.Cases.AddRange(open, done);
        db.Notifications.AddRange(
            Queued(owner.Id, open, NotificationType.NewEmail, DateTimeOffset.UtcNow.AddMinutes(-30)),
            Queued(owner.Id, open, NotificationType.Reminder, DateTimeOffset.UtcNow.AddMinutes(-5)),
            Queued(owner.Id, done, NotificationType.NewEmail, DateTimeOffset.UtcNow.AddMinutes(-10)),
            Queued(owner.Id, open, NotificationType.NewEmail, DateTimeOffset.UtcNow.AddDays(-2)));
        await db.SaveChangesAsync();
        var dispatcher = new FakeAgentNotificationDispatcher();
        var service = new NotificationService(db, dispatcher);

        var sent = await service.DeliverMissedAsync(owner.Id, CancellationToken.None);

        Assert.Equal(2, sent);
        // Oldest first, so the latest pop-up ends up on top; a new email opens the Case, a reminder is a reminder.
        Assert.Equal(new[] { AgentPushCommandType.ShowCase, AgentPushCommandType.ShowReminder }, dispatcher.Sent.Select(s => s.Command.Type));
        Assert.Equal(2, await db.Notifications.CountAsync(n => n.Status == NotificationDeliveryStatus.Sent));

        // Delivered once only.
        Assert.Equal(0, await service.DeliverMissedAsync(owner.Id, CancellationToken.None));
    }

    [Fact]
    public async Task DeliverMissed_AgentGoneAgain_LeavesThemQueued()
    {
        using var db = TestDbContext.CreateNew();
        var owner = new Employee { FullName = "Arniel Montero", Email = "arniel.montero@sawo.com" };
        db.Employees.Add(owner);
        var open = CreateCase(owner.Id);
        db.Cases.Add(open);
        db.Notifications.Add(Queued(owner.Id, open, NotificationType.NewEmail, DateTimeOffset.UtcNow.AddMinutes(-3)));
        await db.SaveChangesAsync();

        var sent = await new NotificationService(db, new FakeAgentNotificationDispatcher { ConnectedAgentCount = 0 })
            .DeliverMissedAsync(owner.Id, CancellationToken.None);

        Assert.Equal(0, sent);
        Assert.Equal(NotificationDeliveryStatus.Queued, (await db.Notifications.SingleAsync()).Status);
    }

    [Fact]
    public void ConnectionTracker_KnowsWhichAgentsHaveALiveConnection()
    {
        var tracker = new AgentConnectionTracker();
        var agent = Guid.NewGuid();
        Assert.False(tracker.IsConnected(agent));

        tracker.Add(agent, "c1");
        tracker.Add(agent, "c2");
        Assert.True(tracker.IsConnected(agent));

        tracker.Remove(agent, "c1");
        Assert.True(tracker.IsConnected(agent));
        tracker.Remove(agent, "c2");
        Assert.False(tracker.IsConnected(agent));
    }
}
