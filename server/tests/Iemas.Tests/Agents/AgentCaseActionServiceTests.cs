using Iemas.Application.Agents;
using Iemas.Application.Agents.Dtos;
using Iemas.Application.Cases;
using Iemas.Application.Reminders;
using Iemas.Domain.Agents;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Iemas.Domain.Identity;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iemas.Tests.Agents;

public class AgentCaseActionServiceTests
{
    private static Employee CreateEmployee() => new() { FullName = "John Smith", Email = "john@sawo.com" };

    private static Case CreateCaseForEmployee(Guid ownerEmployeeId, CaseWorkStatus workStatus = CaseWorkStatus.ActionRequired, CaseReplyStatus replyStatus = CaseReplyStatus.AwaitingReply)
    {
        return new Case
        {
            CaseNumber = "CASE-000001",
            EmailAccountId = Guid.NewGuid(),
            CustomerEmailAddress = "customer@example.com",
            Subject = "Price Request",
            NormalizedSubject = "Price Request",
            OwnerEmployeeId = ownerEmployeeId,
            WorkStatus = workStatus,
            ReplyStatus = replyStatus,
            FirstEmailReceivedAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow,
        };
    }

    private static AgentCaseActionService CreateService(TestDbContext db) =>
        new(db, new CaseWorkflowService(db, new CaseMatchingService(db), new ReminderSchedulingService(db)), new ReminderSchedulingService(db));

    /// <summary>
    /// §43/§46 hard requirement: ALREADY_REPLIED must record only a claim — it must NEVER set
    /// Case.ReplyStatus itself. Only Phase 6's ReplyVerificationService may do that.
    /// </summary>
    [Fact]
    public async Task SubmitActionAsync_AlreadyReplied_NeverMutatesReplyStatus_OnlyRecordsClaimEvent()
    {
        using var db = TestDbContext.CreateNew();
        var employee = CreateEmployee();
        db.Employees.Add(employee);
        var theCase = CreateCaseForEmployee(employee.Id, replyStatus: CaseReplyStatus.AwaitingReply);
        db.Cases.Add(theCase);
        await db.SaveChangesAsync();
        var agentId = Guid.NewGuid();
        var service = CreateService(db);

        var result = await service.SubmitActionAsync(agentId, employee.Id,
            new SubmitCaseActionRequest("req-1", theCase.Id, CaseActionType.AlreadyReplied, null, DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.True(result.Succeeded);
        var reloaded = await db.Cases.SingleAsync();
        // Still AwaitingReply — completely untouched by the claim.
        Assert.Equal(CaseReplyStatus.AwaitingReply, reloaded.ReplyStatus);

        var events = await db.CaseEvents.Where(e => e.CaseId == theCase.Id).ToListAsync();
        var claimEvent = Assert.Single(events, e => e.EventType == CaseEventType.EmployeeAction);
        Assert.Contains("claim", claimEvent.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not a verified fact", claimEvent.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(employee.Id, claimEvent.ActorEmployeeId);
    }

    /// <summary>Same invariant, proven again against a Case that Phase 6 had already verified as NoReplyFound — the claim must not silently "fix" that.</summary>
    [Fact]
    public async Task SubmitActionAsync_AlreadyReplied_DoesNotOverrideAnExistingVerifiedNoReplyFoundStatus()
    {
        using var db = TestDbContext.CreateNew();
        var employee = CreateEmployee();
        db.Employees.Add(employee);
        var theCase = CreateCaseForEmployee(employee.Id, replyStatus: CaseReplyStatus.NoReplyFound);
        db.Cases.Add(theCase);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await service.SubmitActionAsync(Guid.NewGuid(), employee.Id,
            new SubmitCaseActionRequest("req-1", theCase.Id, CaseActionType.AlreadyReplied, "I definitely sent it", DateTimeOffset.UtcNow), CancellationToken.None);

        var reloaded = await db.Cases.SingleAsync();
        Assert.Equal(CaseReplyStatus.NoReplyFound, reloaded.ReplyStatus);
    }

    [Theory]
    [InlineData(CaseActionType.WillHandle, CaseWorkStatus.InProgress)]
    [InlineData(CaseActionType.WaitingForCustomer, CaseWorkStatus.WaitingForCustomer)]
    [InlineData(CaseActionType.WaitingForInternal, CaseWorkStatus.WaitingForInternal)]
    public async Task SubmitActionAsync_StatusChangingActions_UpdateWorkStatus(CaseActionType actionType, CaseWorkStatus expectedStatus)
    {
        using var db = TestDbContext.CreateNew();
        var employee = CreateEmployee();
        db.Employees.Add(employee);
        var theCase = CreateCaseForEmployee(employee.Id);
        db.Cases.Add(theCase);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await service.SubmitActionAsync(Guid.NewGuid(), employee.Id,
            new SubmitCaseActionRequest("req-1", theCase.Id, actionType, null, DateTimeOffset.UtcNow), CancellationToken.None);

        var reloaded = await db.Cases.SingleAsync();
        Assert.Equal(expectedStatus, reloaded.WorkStatus);
    }

    /// <summary>§73 authorization: an Agent may only act on Cases owned by its own linked Employee.</summary>
    [Fact]
    public async Task SubmitActionAsync_CaseOwnedByDifferentEmployee_Fails()
    {
        using var db = TestDbContext.CreateNew();
        var employee = CreateEmployee();
        var otherEmployee = new Employee { FullName = "Someone Else", Email = "other@sawo.com" };
        db.Employees.AddRange(employee, otherEmployee);
        var theCase = CreateCaseForEmployee(otherEmployee.Id);
        db.Cases.Add(theCase);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var result = await service.SubmitActionAsync(Guid.NewGuid(), employee.Id,
            new SubmitCaseActionRequest("req-1", theCase.Id, CaseActionType.Acknowledged, null, DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(0, await db.CaseEvents.CountAsync());
    }

    [Fact]
    public async Task SubmitActionAsync_UnknownCase_Fails()
    {
        using var db = TestDbContext.CreateNew();
        var employee = CreateEmployee();
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var result = await service.SubmitActionAsync(Guid.NewGuid(), employee.Id,
            new SubmitCaseActionRequest("req-1", Guid.NewGuid(), CaseActionType.Acknowledged, null, DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    /// <summary>§73/§78 idempotency — the same (Agent, RequestId) pair replayed must not apply the action twice or create a second CaseEvent.</summary>
    [Fact]
    public async Task SubmitActionAsync_ReplayedRequestId_IsIdempotent_DoesNotDuplicateEffect()
    {
        using var db = TestDbContext.CreateNew();
        var employee = CreateEmployee();
        db.Employees.Add(employee);
        var theCase = CreateCaseForEmployee(employee.Id);
        db.Cases.Add(theCase);
        await db.SaveChangesAsync();
        var agentId = Guid.NewGuid();
        var service = CreateService(db);

        var request = new SubmitCaseActionRequest("req-idempotent-1", theCase.Id, CaseActionType.WillHandle, null, DateTimeOffset.UtcNow);
        var first = await service.SubmitActionAsync(agentId, employee.Id, request, CancellationToken.None);
        var second = await service.SubmitActionAsync(agentId, employee.Id, request, CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.False(first.Value!.WasIdempotentReplay);
        Assert.True(second.Succeeded);
        Assert.True(second.Value!.WasIdempotentReplay);

        Assert.Equal(1, await db.CaseEvents.CountAsync(e => e.CaseId == theCase.Id));
        Assert.Equal(1, await db.AgentCaseActions.CountAsync());
    }

    /// <summary>Same RequestId from a DIFFERENT Agent is a distinct action, not a replay (the idempotency key is scoped per-Agent).</summary>
    [Fact]
    public async Task SubmitActionAsync_SameRequestIdFromDifferentAgent_IsNotTreatedAsReplay()
    {
        using var db = TestDbContext.CreateNew();
        var employee = CreateEmployee();
        db.Employees.Add(employee);
        var theCase = CreateCaseForEmployee(employee.Id);
        db.Cases.Add(theCase);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var request = new SubmitCaseActionRequest("shared-request-id", theCase.Id, CaseActionType.Acknowledged, null, DateTimeOffset.UtcNow);
        var first = await service.SubmitActionAsync(Guid.NewGuid(), employee.Id, request, CancellationToken.None);
        var second = await service.SubmitActionAsync(Guid.NewGuid(), employee.Id, request, CancellationToken.None);

        Assert.False(first.Value!.WasIdempotentReplay);
        Assert.False(second.Value!.WasIdempotentReplay);
        Assert.Equal(2, await db.AgentCaseActions.CountAsync());
    }

    /// <summary>§48 boundary — MARK_COMPLETED via the generic action path must NOT complete the Case; completion requires the dedicated reason-requiring endpoint.</summary>
    [Fact]
    public async Task SubmitActionAsync_MarkCompleted_DoesNotActuallyCompleteTheCase()
    {
        using var db = TestDbContext.CreateNew();
        var employee = CreateEmployee();
        db.Employees.Add(employee);
        var theCase = CreateCaseForEmployee(employee.Id);
        db.Cases.Add(theCase);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await service.SubmitActionAsync(Guid.NewGuid(), employee.Id,
            new SubmitCaseActionRequest("req-1", theCase.Id, CaseActionType.MarkCompleted, null, DateTimeOffset.UtcNow), CancellationToken.None);

        var reloaded = await db.Cases.SingleAsync();
        Assert.NotEqual(CaseWorkStatus.Completed, reloaded.WorkStatus);
        Assert.Null(reloaded.CompletionReason);
    }

    [Fact]
    public async Task SubmitActionAsync_Reopen_OnCompletedCase_ReopensAndIncrementsCount()
    {
        using var db = TestDbContext.CreateNew();
        var employee = CreateEmployee();
        db.Employees.Add(employee);
        var theCase = CreateCaseForEmployee(employee.Id, workStatus: CaseWorkStatus.Completed);
        db.Cases.Add(theCase);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await service.SubmitActionAsync(Guid.NewGuid(), employee.Id,
            new SubmitCaseActionRequest("req-1", theCase.Id, CaseActionType.Reopen, null, DateTimeOffset.UtcNow), CancellationToken.None);

        var reloaded = await db.Cases.SingleAsync();
        Assert.Equal(CaseWorkStatus.ActionRequired, reloaded.WorkStatus);
        Assert.Equal(1, reloaded.ReopenCount);
    }

    /// <summary>§47 — a standalone comment is recorded distinctly from a status-changing action.</summary>
    [Fact]
    public async Task SubmitCommentAsync_RecordsCommentEvent_DoesNotChangeWorkStatus()
    {
        using var db = TestDbContext.CreateNew();
        var employee = CreateEmployee();
        db.Employees.Add(employee);
        var theCase = CreateCaseForEmployee(employee.Id);
        db.Cases.Add(theCase);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var result = await service.SubmitCommentAsync(Guid.NewGuid(), employee.Id,
            new SubmitCaseCommentRequest("req-1", theCase.Id, "I already called the client.", DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.True(result.Succeeded);
        var reloaded = await db.Cases.SingleAsync();
        Assert.Equal(CaseWorkStatus.ActionRequired, reloaded.WorkStatus);

        var commentEvent = await db.CaseEvents.SingleAsync(e => e.CaseId == theCase.Id);
        Assert.Equal(CaseEventType.EmployeeComment, commentEvent.EventType);
        Assert.Equal("I already called the client.", commentEvent.Detail);
    }

    [Fact]
    public async Task SubmitCommentAsync_EmptyComment_Fails()
    {
        using var db = TestDbContext.CreateNew();
        var employee = CreateEmployee();
        db.Employees.Add(employee);
        var theCase = CreateCaseForEmployee(employee.Id);
        db.Cases.Add(theCase);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var result = await service.SubmitCommentAsync(Guid.NewGuid(), employee.Id,
            new SubmitCaseCommentRequest("req-1", theCase.Id, "   ", DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    /// <summary>§56 "Remind me at 3:00 PM" — a RemindLater action, once persisted, actually schedules a Reminder via ReminderSchedulingService (Phase 8 wiring), not just a bare CaseEvent as in earlier phases.</summary>
    [Fact]
    public async Task SubmitActionAsync_RemindLater_SchedulesReminder()
    {
        using var db = TestDbContext.CreateNew();
        db.ReminderPolicies.Add(new Iemas.Domain.Reminders.ReminderPolicy
        {
            Name = "Default", Enabled = true, IsDefault = true,
            InitialDelay = TimeSpan.FromHours(4), ReminderInterval = TimeSpan.FromHours(24),
            MaxReminders = 3, MinimumInterval = TimeSpan.FromHours(1),
            RestrictToBusinessHours = false, ExcludeWeekends = false, TimeZoneId = "UTC",
        });
        var employee = CreateEmployee();
        db.Employees.Add(employee);
        var theCase = CreateCaseForEmployee(employee.Id);
        db.Cases.Add(theCase);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var requestedFor = DateTimeOffset.UtcNow.AddHours(3);
        var result = await service.SubmitActionAsync(Guid.NewGuid(), employee.Id,
            new SubmitCaseActionRequest("req-remind-1", theCase.Id, CaseActionType.RemindLater, null, DateTimeOffset.UtcNow, requestedFor),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        var actionRecord = await db.AgentCaseActions.SingleAsync(a => a.RequestId == "req-remind-1");
        var reminder = await db.Reminders.SingleOrDefaultAsync(r => r.SourceAgentCaseActionId == actionRecord.Id);
        Assert.NotNull(reminder);
        Assert.Equal(requestedFor, reminder!.ScheduledForUtc);
    }

    /// <summary>§78 idempotency — a retried RemindLater request (same RequestId) must not schedule a second Reminder.</summary>
    [Fact]
    public async Task SubmitActionAsync_RemindLaterRetried_DoesNotDuplicateReminder()
    {
        using var db = TestDbContext.CreateNew();
        db.ReminderPolicies.Add(new Iemas.Domain.Reminders.ReminderPolicy
        {
            Name = "Default", Enabled = true, IsDefault = true,
            InitialDelay = TimeSpan.FromHours(4), ReminderInterval = TimeSpan.FromHours(24),
            MaxReminders = 3, MinimumInterval = TimeSpan.FromHours(1),
            RestrictToBusinessHours = false, ExcludeWeekends = false, TimeZoneId = "UTC",
        });
        var employee = CreateEmployee();
        db.Employees.Add(employee);
        var theCase = CreateCaseForEmployee(employee.Id);
        db.Cases.Add(theCase);
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var agentId = Guid.NewGuid();

        var request = new SubmitCaseActionRequest("req-remind-retry", theCase.Id, CaseActionType.RemindLater, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(3));
        await service.SubmitActionAsync(agentId, employee.Id, request, CancellationToken.None);
        var second = await service.SubmitActionAsync(agentId, employee.Id, request, CancellationToken.None);

        Assert.True(second.Succeeded);
        Assert.True(second.Value!.WasIdempotentReplay);
        Assert.Equal(1, await db.Reminders.CountAsync(r => r.CaseId == theCase.Id));
    }

    /// <summary>
    /// §48/§46 MARK_COMPLETED — this is the Agent-reachable path that CasesController's
    /// admin-only /complete endpoint cannot serve (an Agent's bearer token can never satisfy
    /// RequireAdministrator). Confirms it actually completes the Case with the given reason.
    /// </summary>
    [Fact]
    public async Task CompleteCaseAsync_OwnedCase_CompletesWithReasonAndComment()
    {
        using var db = TestDbContext.CreateNew();
        var employee = CreateEmployee();
        db.Employees.Add(employee);
        var theCase = CreateCaseForEmployee(employee.Id);
        db.Cases.Add(theCase);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var result = await service.CompleteCaseAsync(Guid.NewGuid(), employee.Id, theCase.Id, CaseCompletionReason.HandledOutsideEmail, "Called the customer.", CancellationToken.None);

        Assert.True(result.Succeeded);
        var reloaded = await db.Cases.AsNoTracking().SingleAsync(c => c.Id == theCase.Id);
        Assert.Equal(CaseWorkStatus.Completed, reloaded.WorkStatus);
        Assert.Equal(CaseCompletionReason.HandledOutsideEmail, reloaded.CompletionReason);
        Assert.Equal("Called the customer.", reloaded.CompletionComment);
        Assert.NotNull(reloaded.CompletedAt);
    }

    /// <summary>§73 "Server must validate that the Agent is authorized for the Employee and Case" — an Agent cannot complete a Case it does not own, even with a well-formed request.</summary>
    [Fact]
    public async Task CompleteCaseAsync_NotOwner_Fails_AndDoesNotCompleteCase()
    {
        using var db = TestDbContext.CreateNew();
        var owner = CreateEmployee();
        var imposter = new Employee { FullName = "Someone Else", Email = "someone@sawo.com" };
        db.Employees.AddRange(owner, imposter);
        var theCase = CreateCaseForEmployee(owner.Id);
        db.Cases.Add(theCase);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var result = await service.CompleteCaseAsync(Guid.NewGuid(), imposter.Id, theCase.Id, CaseCompletionReason.HandledOutsideEmail, null, CancellationToken.None);

        Assert.False(result.Succeeded);
        var reloaded = await db.Cases.AsNoTracking().SingleAsync(c => c.Id == theCase.Id);
        Assert.NotEqual(CaseWorkStatus.Completed, reloaded.WorkStatus);
    }
}
