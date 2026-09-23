// Tests the controller's own action logic (service invocation, result mapping); [Authorize] policy
// enforcement itself requires the full HTTP pipeline and is not exercised here — same acknowledged
// boundary as every prior phase's RBAC verification gap.
using Iemas.Api.Controllers;
using Iemas.Application.Escalations;
using Iemas.Application.Escalations.Dtos;
using Iemas.Domain.Cases;
using Iemas.Domain.Escalations;
using Iemas.Domain.Identity;
using Iemas.Domain.Reminders;
using Iemas.Tests.TestSupport;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Iemas.Tests.Escalations;

public class EscalationsControllerTests
{
    private static EscalationsController CreateController(TestDbContext db) =>
        new(new EscalationQueryService(db), new EscalationService(db));

    private static Case CreateCase()
    {
        return new Case
        {
            CaseNumber = $"CASE-{Guid.NewGuid():N}"[..12],
            EmailAccountId = Guid.NewGuid(),
            CustomerEmailAddress = "customer@example.com",
            Subject = "Price Request",
            NormalizedSubject = "Price Request",
            WorkStatus = CaseWorkStatus.ActionRequired,
            ReplyStatus = CaseReplyStatus.AwaitingReply,
            FirstEmailReceivedAt = DateTimeOffset.UtcNow.AddDays(-5),
            LastActivityAt = DateTimeOffset.UtcNow,
        };
    }

    [Fact]
    public async Task Search_NoCaseId_ReturnsOk_WithEvents()
    {
        using var db = TestDbContext.CreateNew();
        var owner = new Employee { FullName = "John Smith", Email = "john@sawo.com", IsActive = true };
        db.Employees.Add(owner);
        var policy = new EscalationPolicy { Name = "Default", Enabled = true, IsDefault = true, TriggerReminderCount = 1, MaximumLevel = 3, Channel = "Email" };
        db.EscalationPolicies.Add(policy);
        db.EscalationLevels.Add(new EscalationLevel { EscalationPolicyId = policy.Id, Level = 1, RecipientType = EscalationRecipientType.Employee });
        var targetCase = CreateCase();
        targetCase.OwnerEmployeeId = owner.Id;
        db.Cases.Add(targetCase);
        db.Reminders.Add(new Reminder
        {
            CaseId = targetCase.Id, Trigger = ReminderTrigger.InitialActionRequired, Status = ReminderStatus.Sent,
            SequenceNumber = 1, ScheduledForUtc = DateTimeOffset.UtcNow.AddHours(-1), ExecutedAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
        });
        await db.SaveChangesAsync();

        var escalationService = new EscalationService(db);
        await escalationService.EvaluateCaseAsync(targetCase.Id, CancellationToken.None);

        var controller = new EscalationsController(new EscalationQueryService(db), escalationService);
        var result = await controller.Search(null, null, null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsAssignableFrom<List<EscalationEventDto>>(ok.Value);
        Assert.Single(list);
    }

    [Fact]
    public async Task Search_WithCaseId_ReturnsOnlyThatCasesEvents()
    {
        using var db = TestDbContext.CreateNew();
        var targetCase = CreateCase();
        db.Cases.Add(targetCase);
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var result = await controller.Search(targetCase.Id, null, null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsAssignableFrom<List<EscalationEventDto>>(ok.Value);
        Assert.Empty(list);
    }

    /// <summary>Manual §111 escalation-run endpoint returns the aggregate EscalationRunResult.</summary>
    [Fact]
    public async Task Run_ReturnsOk_WithEscalationRunResult()
    {
        using var db = TestDbContext.CreateNew();
        var controller = CreateController(db);

        var result = await controller.Run(10, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<EscalationRunResult>(ok.Value);
    }
}
