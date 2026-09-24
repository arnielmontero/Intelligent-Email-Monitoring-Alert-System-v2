using Iemas.Application.Cases;
using Iemas.Domain.Cases;
using Iemas.Domain.Identity;
using Iemas.Tests.TestSupport;
using Xunit;

namespace Iemas.Tests.Cases;

public class CaseServiceTests
{
    private static Case CreateCase(string caseNumber) => new()
    {
        CaseNumber = caseNumber,
        EmailAccountId = Guid.NewGuid(),
        CustomerEmailAddress = "customer@example.com",
        Subject = "Test subject",
        NormalizedSubject = "Test subject",
        WorkStatus = CaseWorkStatus.ActionRequired,
        ReplyStatus = CaseReplyStatus.AwaitingReply,
        FirstEmailReceivedAt = DateTimeOffset.UtcNow,
        LastActivityAt = DateTimeOffset.UtcNow,
    };

    /// <summary>§66/§89 — global, cross-Case search, newest first, correctly carrying Case-identifying info alongside each event.</summary>
    [Fact]
    public async Task SearchEventsAsync_ReturnsNewestFirst_WithCaseInfo()
    {
        using var db = TestDbContext.CreateNew();
        var case1 = CreateCase("CASE-000001");
        var case2 = CreateCase("CASE-000002");
        db.Cases.AddRange(case1, case2);
        await db.SaveChangesAsync();

        db.CaseEvents.Add(new CaseEvent { CaseId = case1.Id, EventType = CaseEventType.Created, Detail = "First", OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-2) });
        db.CaseEvents.Add(new CaseEvent { CaseId = case2.Id, EventType = CaseEventType.EmployeeComment, Detail = "Second", OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        await db.SaveChangesAsync();

        var service = new CaseService(db);
        var results = await service.SearchEventsAsync(null, null, null, null, 100, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal("Second", results[0].Detail);
        Assert.Equal("CASE-000002", results[0].CaseNumber);
        Assert.Equal("First", results[1].Detail);
    }

    [Fact]
    public async Task SearchEventsAsync_FilterByCaseId_ReturnsOnlyThatCase()
    {
        using var db = TestDbContext.CreateNew();
        var case1 = CreateCase("CASE-000001");
        var case2 = CreateCase("CASE-000002");
        db.Cases.AddRange(case1, case2);
        await db.SaveChangesAsync();

        db.CaseEvents.Add(new CaseEvent { CaseId = case1.Id, EventType = CaseEventType.Created, Detail = "A" });
        db.CaseEvents.Add(new CaseEvent { CaseId = case2.Id, EventType = CaseEventType.Created, Detail = "B" });
        await db.SaveChangesAsync();

        var service = new CaseService(db);
        var results = await service.SearchEventsAsync(case1.Id, null, null, null, 100, CancellationToken.None);

        var only = Assert.Single(results);
        Assert.Equal("A", only.Detail);
    }

    [Fact]
    public async Task SearchEventsAsync_FilterByEventType_ReturnsOnlyMatching()
    {
        using var db = TestDbContext.CreateNew();
        var theCase = CreateCase("CASE-000001");
        db.Cases.Add(theCase);
        await db.SaveChangesAsync();

        db.CaseEvents.Add(new CaseEvent { CaseId = theCase.Id, EventType = CaseEventType.Created, Detail = "Create" });
        db.CaseEvents.Add(new CaseEvent { CaseId = theCase.Id, EventType = CaseEventType.EmployeeComment, Detail = "Comment" });
        await db.SaveChangesAsync();

        var service = new CaseService(db);
        var results = await service.SearchEventsAsync(null, CaseEventType.EmployeeComment, null, null, 100, CancellationToken.None);

        var only = Assert.Single(results);
        Assert.Equal("Comment", only.Detail);
    }

    [Fact]
    public async Task SearchEventsAsync_ActorEmployeeName_ResolvedFromEmployeeId()
    {
        using var db = TestDbContext.CreateNew();
        var employee = new Employee { FullName = "Jane Doe", Email = "jane@sawo.com" };
        db.Employees.Add(employee);
        var theCase = CreateCase("CASE-000001");
        db.Cases.Add(theCase);
        await db.SaveChangesAsync();

        db.CaseEvents.Add(new CaseEvent { CaseId = theCase.Id, EventType = CaseEventType.EmployeeAction, Detail = "Acknowledged", ActorEmployeeId = employee.Id });
        await db.SaveChangesAsync();

        var service = new CaseService(db);
        var results = await service.SearchEventsAsync(null, null, null, null, 100, CancellationToken.None);

        var only = Assert.Single(results);
        Assert.Equal("Jane Doe", only.ActorEmployeeName);
    }
}
