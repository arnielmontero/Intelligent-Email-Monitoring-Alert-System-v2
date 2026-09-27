using Iemas.Application.EmployeeActivity;
using Iemas.Domain.Agents;
using Iemas.Domain.Cases;
using Iemas.Domain.Identity;
using Iemas.Domain.Notifications;
using Iemas.Tests.TestSupport;

namespace Iemas.Tests.EmployeeActivity;

public class EmployeeActivityQueryServiceTests
{
    private sealed record Fixture(Employee John, Employee Mary, Case Case, Agent Agent);

    private static async Task<Fixture> SeedAsync(TestDbContext db)
    {
        var john = new Employee { FullName = "John", Email = "john@sawo.com" };
        var mary = new Employee { FullName = "Mary", Email = "mary@sawo.com" };
        db.Employees.AddRange(john, mary);

        var targetCase = new Case
        {
            CaseNumber = "CASE-000001", EmailAccountId = Guid.NewGuid(), CustomerEmailAddress = "c@example.com",
            Subject = "Price Request", NormalizedSubject = "Price Request", OwnerEmployeeId = john.Id,
            WorkStatus = CaseWorkStatus.InProgress, FirstEmailReceivedAt = DateTimeOffset.UtcNow, LastActivityAt = DateTimeOffset.UtcNow,
        };
        db.Cases.Add(targetCase);

        var agent = new Agent
        {
            ClientName = "JOHN-LAPTOP", EnrollmentEmailAddress = "john@sawo.com", EmployeeId = john.Id,
            RegistrationStatus = AgentRegistrationStatus.Approved, ConnectionStatus = AgentConnectionStatus.Connected,
            RegistrationRequestToken = Guid.NewGuid().ToString(),
        };
        db.Agents.Add(agent);

        var ackEvent = new CaseEvent { CaseId = targetCase.Id, EventType = CaseEventType.EmployeeAction, Detail = "ack" };
        var commentEvent = new CaseEvent { CaseId = targetCase.Id, EventType = CaseEventType.EmployeeComment, Detail = "Called the customer" };
        db.CaseEvents.AddRange(ackEvent, commentEvent);

        db.AgentCaseActions.AddRange(
            new AgentCaseAction
            {
                RequestId = "r1", AgentId = agent.Id, EmployeeId = john.Id, CaseId = targetCase.Id, ActionType = CaseActionType.Acknowledged,
                CaseEventId = ackEvent.Id, ServerTimestamp = DateTimeOffset.UtcNow.AddHours(-3),
            },
            new AgentCaseAction
            {
                RequestId = "r2", AgentId = agent.Id, EmployeeId = john.Id, CaseId = targetCase.Id, ActionType = CaseActionType.Acknowledged,
                Comment = "Called the customer", CaseEventId = commentEvent.Id, ServerTimestamp = DateTimeOffset.UtcNow.AddHours(-2),
            },
            new AgentCaseAction
            {
                RequestId = "r3", AgentId = agent.Id, EmployeeId = john.Id, CaseId = targetCase.Id, ActionType = CaseActionType.MarkCompleted,
                ServerTimestamp = DateTimeOffset.UtcNow.AddHours(-1),
            });

        db.Notifications.AddRange(
            new Notification { CaseId = targetCase.Id, EmployeeId = john.Id, Status = NotificationDeliveryStatus.Acknowledged, Title = "t", Message = "m" },
            new Notification { CaseId = targetCase.Id, EmployeeId = john.Id, Status = NotificationDeliveryStatus.Sent, Title = "t", Message = "m" });

        await db.SaveChangesAsync();
        return new Fixture(john, mary, targetCase, agent);
    }

    /// <summary>A standalone comment is reported as "Comment", not as the placeholder Acknowledged action type it is stored with.</summary>
    [Fact]
    public async Task SearchAsync_DistinguishesCommentsFromActions_NewestFirst()
    {
        using var db = TestDbContext.CreateNew();
        await SeedAsync(db);

        var rows = await new EmployeeActivityQueryService(db).SearchAsync(null, null, null, null, null, 100, CancellationToken.None);

        Assert.Equal(new[] { "MarkCompleted", "Comment", "Acknowledged" }, rows.Select(r => r.Activity));
        Assert.Equal("JOHN-LAPTOP", rows[0].AgentName);
        Assert.Equal("CASE-000001", rows[0].CaseNumber);
    }

    [Fact]
    public async Task SearchAsync_FilterByActivity()
    {
        using var db = TestDbContext.CreateNew();
        await SeedAsync(db);
        var service = new EmployeeActivityQueryService(db);

        Assert.Single(await service.SearchAsync(null, null, "Comment", null, null, 100, CancellationToken.None));
        var acknowledged = Assert.Single(await service.SearchAsync(null, null, "Acknowledged", null, null, 100, CancellationToken.None));
        Assert.Null(acknowledged.Comment);
    }

    [Fact]
    public async Task GetSummaryAsync_CountsPerEmployee()
    {
        using var db = TestDbContext.CreateNew();
        var fixture = await SeedAsync(db);

        var summary = await new EmployeeActivityQueryService(db).GetSummaryAsync(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

        var john = summary.Single(s => s.EmployeeId == fixture.John.Id);
        Assert.Equal(2, john.ActionCount);
        Assert.Equal(1, john.AcknowledgedCount);
        Assert.Equal(1, john.CompletedCount);
        Assert.Equal(1, john.CommentCount);
        Assert.Equal(2, john.NotificationCount);
        Assert.Equal(1, john.NotificationsAcknowledgedCount);
        Assert.Equal(1, john.OpenCaseCount);
        Assert.Equal(1, john.ConnectedAgentCount);
        Assert.NotNull(john.LastActivityAt);

        var mary = summary.Single(s => s.EmployeeId == fixture.Mary.Id);
        Assert.Equal(0, mary.ActionCount);
        Assert.Null(mary.LastActivityAt);
    }
}
