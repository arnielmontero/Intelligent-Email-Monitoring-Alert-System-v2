using Iemas.Application.Escalations;
using Iemas.Domain.Ai;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Iemas.Domain.Escalations;
using Iemas.Domain.Identity;
using Iemas.Domain.Reminders;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iemas.Tests.Escalations;

public class EscalationServiceTests
{
    private static EscalationPolicy CreateDefaultPolicy(
        bool enabled = true, int triggerReminderCount = 1, TimeSpan? gracePeriod = null, TimeSpan? cooldown = null, int maximumLevel = 3)
    {
        return new EscalationPolicy
        {
            Name = "Default",
            Enabled = enabled,
            IsDefault = true,
            TriggerReminderCount = triggerReminderCount,
            GracePeriod = gracePeriod ?? TimeSpan.Zero,
            Cooldown = cooldown ?? TimeSpan.Zero,
            MaximumLevel = maximumLevel,
            Channel = "Email",
        };
    }

    private static EscalationLevel CreateLevel(Guid policyId, int level, EscalationRecipientType recipientType, TimeSpan? delay = null, Guid? specificEmployeeId = null, Guid? specificGroupId = null)
    {
        return new EscalationLevel
        {
            EscalationPolicyId = policyId,
            Level = level,
            DelayAfterPreviousLevel = delay ?? TimeSpan.Zero,
            RecipientType = recipientType,
            SpecificEmployeeId = specificEmployeeId,
            SpecificGroupId = specificGroupId,
        };
    }

    private static Case CreateCase(
        Guid? ownerEmployeeId = null, CaseWorkStatus workStatus = CaseWorkStatus.ActionRequired, CaseReplyStatus replyStatus = CaseReplyStatus.AwaitingReply,
        DateTimeOffset? firstEmailReceivedAt = null)
    {
        return new Case
        {
            CaseNumber = $"CASE-{Guid.NewGuid():N}"[..12],
            EmailAccountId = Guid.NewGuid(),
            CustomerEmailAddress = "customer@example.com",
            Subject = "Price Request",
            NormalizedSubject = "Price Request",
            OwnerEmployeeId = ownerEmployeeId,
            WorkStatus = workStatus,
            ReplyStatus = replyStatus,
            FirstEmailReceivedAt = firstEmailReceivedAt ?? DateTimeOffset.UtcNow.AddDays(-5),
            LastActivityAt = DateTimeOffset.UtcNow,
        };
    }

    private static Reminder CreateSentReminder(Guid caseId, int sequence = 1)
    {
        return new Reminder
        {
            CaseId = caseId,
            Trigger = ReminderTrigger.InitialActionRequired,
            Status = ReminderStatus.Sent,
            SequenceNumber = sequence,
            ScheduledForUtc = DateTimeOffset.UtcNow.AddHours(-1),
            ExecutedAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
        };
    }

    /// <summary>§60.10 / core happy path — a fully eligible Case with a resolvable Employee-owner recipient escalates to Level 1 and is recorded Executed.</summary>
    [Fact]
    public async Task EvaluateCaseAsync_FullyEligibleCase_ExecutesLevel1_ResolvesOwnerAsRecipient()
    {
        using var db = TestDbContext.CreateNew();
        var owner = new Employee { FullName = "John Smith", Email = "john@sawo.com", IsActive = true };
        db.Employees.Add(owner);
        var policy = CreateDefaultPolicy();
        db.EscalationPolicies.Add(policy);
        db.EscalationLevels.Add(CreateLevel(policy.Id, 1, EscalationRecipientType.Employee));
        var targetCase = CreateCase(ownerEmployeeId: owner.Id);
        db.Cases.Add(targetCase);
        db.Reminders.Add(CreateSentReminder(targetCase.Id));
        await db.SaveChangesAsync();

        var service = new EscalationService(db);
        var outcome = await service.EvaluateCaseAsync(targetCase.Id, CancellationToken.None);

        Assert.Equal(EscalationOutcome.Executed, outcome);
        var evt = await db.EscalationEvents.SingleAsync(e => e.CaseId == targetCase.Id);
        Assert.Equal(1, evt.Level);
        Assert.Equal(EscalationOutcome.Executed, evt.Outcome);
        Assert.Contains("John Smith", evt.RecipientDisplay);

        var reloadedCase = await db.Cases.AsNoTracking().SingleAsync();
        Assert.Equal(CaseWorkStatus.Escalated, reloadedCase.WorkStatus);
        // §64 — ownership must NOT change.
        Assert.Equal(owner.Id, reloadedCase.OwnerEmployeeId);
    }

    /// <summary>§60.1 — a Case that no longer exists is recorded as a skip, not an exception.</summary>
    [Fact]
    public async Task EvaluateCaseAsync_CaseNotFound_RecordsSkip()
    {
        using var db = TestDbContext.CreateNew();
        var service = new EscalationService(db);

        var outcome = await service.EvaluateCaseAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(EscalationOutcome.Skipped, outcome);
    }

    /// <summary>§60.2/§60.6 — a Completed Case never escalates.</summary>
    [Fact]
    public async Task EvaluateCaseAsync_CompletedCase_Skipped()
    {
        using var db = TestDbContext.CreateNew();
        db.EscalationPolicies.Add(CreateDefaultPolicy());
        var targetCase = CreateCase(workStatus: CaseWorkStatus.Completed);
        db.Cases.Add(targetCase);
        db.Reminders.Add(CreateSentReminder(targetCase.Id));
        await db.SaveChangesAsync();

        var service = new EscalationService(db);
        var outcome = await service.EvaluateCaseAsync(targetCase.Id, CancellationToken.None);

        Assert.Equal(EscalationOutcome.Skipped, outcome);
        var evt = await db.EscalationEvents.SingleAsync();
        Assert.Equal(EscalationSkipReason.CaseNotActive, evt.SkipReason);
    }

    /// <summary>§60.7 — a Cancelled Case never escalates.</summary>
    [Fact]
    public async Task EvaluateCaseAsync_CancelledCase_Skipped()
    {
        using var db = TestDbContext.CreateNew();
        db.EscalationPolicies.Add(CreateDefaultPolicy());
        var targetCase = CreateCase(workStatus: CaseWorkStatus.Cancelled);
        db.Cases.Add(targetCase);
        db.Reminders.Add(CreateSentReminder(targetCase.Id));
        await db.SaveChangesAsync();

        var service = new EscalationService(db);
        var outcome = await service.EvaluateCaseAsync(targetCase.Id, CancellationToken.None);

        var evt = await db.EscalationEvents.SingleAsync();
        Assert.Equal(EscalationSkipReason.CaseNotActive, evt.SkipReason);
    }

    /// <summary>§60.4/§60.5 — a verified reply resolves the reply requirement; escalation is skipped, never executed, regardless of reminder count.</summary>
    [Fact]
    public async Task EvaluateCaseAsync_VerifiedReply_Skipped_NeverEscalates()
    {
        using var db = TestDbContext.CreateNew();
        db.EscalationPolicies.Add(CreateDefaultPolicy());
        var targetCase = CreateCase(replyStatus: CaseReplyStatus.Replied);
        db.Cases.Add(targetCase);
        db.Reminders.Add(CreateSentReminder(targetCase.Id));
        await db.SaveChangesAsync();

        var service = new EscalationService(db);
        var outcome = await service.EvaluateCaseAsync(targetCase.Id, CancellationToken.None);

        Assert.Equal(EscalationOutcome.Skipped, outcome);
        var evt = await db.EscalationEvents.SingleAsync();
        Assert.Equal(EscalationSkipReason.ReplyVerified, evt.SkipReason);
    }

    /// <summary>§60.9 — a disabled policy never escalates, even if every other condition is satisfied.</summary>
    [Fact]
    public async Task EvaluateCaseAsync_PolicyDisabled_NoApplicablePolicy()
    {
        using var db = TestDbContext.CreateNew();
        db.EscalationPolicies.Add(CreateDefaultPolicy(enabled: false));
        var targetCase = CreateCase();
        db.Cases.Add(targetCase);
        db.Reminders.Add(CreateSentReminder(targetCase.Id));
        await db.SaveChangesAsync();

        var service = new EscalationService(db);
        var outcome = await service.EvaluateCaseAsync(targetCase.Id, CancellationToken.None);

        Assert.Equal(EscalationOutcome.Skipped, outcome);
        var evt = await db.EscalationEvents.SingleAsync();
        // A disabled policy is never returned by ResolvePolicyAsync at all, so the reason is
        // "no applicable policy," not "policy disabled" — this asserts the actual code path.
        Assert.Equal(EscalationSkipReason.NoApplicablePolicy, evt.SkipReason);
    }

    /// <summary>Reminder → Escalation trigger — below the configured reminder-count threshold, never escalates.</summary>
    [Fact]
    public async Task EvaluateCaseAsync_BelowReminderThreshold_ThresholdNotReached()
    {
        using var db = TestDbContext.CreateNew();
        db.EscalationPolicies.Add(CreateDefaultPolicy(triggerReminderCount: 3));
        var targetCase = CreateCase();
        db.Cases.Add(targetCase);
        db.Reminders.Add(CreateSentReminder(targetCase.Id, sequence: 1)); // only 1 sent, need 3
        await db.SaveChangesAsync();

        var service = new EscalationService(db);
        var outcome = await service.EvaluateCaseAsync(targetCase.Id, CancellationToken.None);

        var evt = await db.EscalationEvents.SingleAsync();
        Assert.Equal(EscalationSkipReason.ThresholdNotReached, evt.SkipReason);
    }

    /// <summary>§57 "Grace Period" — even with enough reminders, escalation waits for the grace period since the Case's first email.</summary>
    [Fact]
    public async Task EvaluateCaseAsync_GracePeriodNotElapsed_ThresholdNotReached()
    {
        using var db = TestDbContext.CreateNew();
        db.EscalationPolicies.Add(CreateDefaultPolicy(gracePeriod: TimeSpan.FromDays(10)));
        var targetCase = CreateCase(firstEmailReceivedAt: DateTimeOffset.UtcNow.AddDays(-1)); // only 1 day old, grace is 10
        db.Cases.Add(targetCase);
        db.Reminders.Add(CreateSentReminder(targetCase.Id));
        await db.SaveChangesAsync();

        var service = new EscalationService(db);
        var outcome = await service.EvaluateCaseAsync(targetCase.Id, CancellationToken.None);

        var evt = await db.EscalationEvents.SingleAsync();
        Assert.Equal(EscalationSkipReason.ThresholdNotReached, evt.SkipReason);
    }

    /// <summary>§60.8 — a level that has already executed for this Case/Policy is never re-executed by a subsequent evaluation.</summary>
    [Fact]
    public async Task EvaluateCaseAsync_LevelAlreadyExecuted_DoesNotReExecute_ProgressesToNextLevel()
    {
        using var db = TestDbContext.CreateNew();
        var owner = new Employee { FullName = "John Smith", Email = "john@sawo.com", IsActive = true };
        db.Employees.Add(owner);
        var policy = CreateDefaultPolicy();
        db.EscalationPolicies.Add(policy);
        db.EscalationLevels.Add(CreateLevel(policy.Id, 1, EscalationRecipientType.Employee));
        db.EscalationLevels.Add(CreateLevel(policy.Id, 2, EscalationRecipientType.Employee));
        var targetCase = CreateCase(ownerEmployeeId: owner.Id);
        db.Cases.Add(targetCase);
        db.Reminders.Add(CreateSentReminder(targetCase.Id));
        await db.SaveChangesAsync();

        var service = new EscalationService(db);
        var first = await service.EvaluateCaseAsync(targetCase.Id, CancellationToken.None);
        var second = await service.EvaluateCaseAsync(targetCase.Id, CancellationToken.None);

        Assert.Equal(EscalationOutcome.Executed, first);
        // Second call is either a Cooldown skip (Cooldown=0 here so no) or straight to Level 2 —
        // with zero cooldown and zero level-delay, it proceeds directly to Level 2, never Level 1 again.
        var events = await db.EscalationEvents.Where(e => e.CaseId == targetCase.Id).OrderBy(e => e.OccurredAt).ToListAsync();
        Assert.DoesNotContain(events, e => e.Level == 1 && e.Outcome == EscalationOutcome.Skipped && e.SkipReason == EscalationSkipReason.LevelAlreadyExecuted);
        var executedLevels = events.Where(e => e.Outcome == EscalationOutcome.Executed).Select(e => e.Level).ToList();
        Assert.Equal(new[] { 1, 2 }, executedLevels);
    }

    /// <summary>§57/§58 "Maximum Level" — once the configured ceiling is reached, no further level executes.</summary>
    [Fact]
    public async Task EvaluateCaseAsync_AtMaximumLevel_NeverEscalatesFurther()
    {
        using var db = TestDbContext.CreateNew();
        var owner = new Employee { FullName = "John Smith", Email = "john@sawo.com", IsActive = true };
        db.Employees.Add(owner);
        var policy = CreateDefaultPolicy(maximumLevel: 1);
        db.EscalationPolicies.Add(policy);
        db.EscalationLevels.Add(CreateLevel(policy.Id, 1, EscalationRecipientType.Employee));
        var targetCase = CreateCase(ownerEmployeeId: owner.Id);
        db.Cases.Add(targetCase);
        db.Reminders.Add(CreateSentReminder(targetCase.Id));
        await db.SaveChangesAsync();

        var service = new EscalationService(db);
        await service.EvaluateCaseAsync(targetCase.Id, CancellationToken.None);
        var second = await service.EvaluateCaseAsync(targetCase.Id, CancellationToken.None);

        Assert.Equal(EscalationOutcome.Skipped, second);
        var lastEvent = await db.EscalationEvents.Where(e => e.CaseId == targetCase.Id).OrderByDescending(e => e.OccurredAt).FirstAsync();
        Assert.Equal(EscalationSkipReason.MaximumLevelReached, lastEvent.SkipReason);
        Assert.Equal(1, await db.EscalationEvents.CountAsync(e => e.Outcome == EscalationOutcome.Executed));
    }

    /// <summary>§57 "Cooldown" — a second level cannot fire before the cooldown since the last executed level has elapsed.</summary>
    [Fact]
    public async Task EvaluateCaseAsync_CooldownActive_SkipsUntilElapsed()
    {
        using var db = TestDbContext.CreateNew();
        var owner = new Employee { FullName = "John Smith", Email = "john@sawo.com", IsActive = true };
        db.Employees.Add(owner);
        var policy = CreateDefaultPolicy(cooldown: TimeSpan.FromDays(1));
        db.EscalationPolicies.Add(policy);
        db.EscalationLevels.Add(CreateLevel(policy.Id, 1, EscalationRecipientType.Employee));
        db.EscalationLevels.Add(CreateLevel(policy.Id, 2, EscalationRecipientType.Employee));
        var targetCase = CreateCase(ownerEmployeeId: owner.Id);
        db.Cases.Add(targetCase);
        db.Reminders.Add(CreateSentReminder(targetCase.Id));
        await db.SaveChangesAsync();

        var service = new EscalationService(db);
        await service.EvaluateCaseAsync(targetCase.Id, CancellationToken.None); // Level 1 executes
        var second = await service.EvaluateCaseAsync(targetCase.Id, CancellationToken.None); // Level 2 should be blocked by cooldown

        Assert.Equal(EscalationOutcome.Skipped, second);
        var lastEvent = await db.EscalationEvents.Where(e => e.CaseId == targetCase.Id).OrderByDescending(e => e.OccurredAt).FirstAsync();
        Assert.Equal(EscalationSkipReason.CooldownActive, lastEvent.SkipReason);
    }

    /// <summary>§60.10 — a Case with no owner cannot resolve an Employee-type recipient; recorded as RecipientUnresolved, never silently treated as success.</summary>
    [Fact]
    public async Task EvaluateCaseAsync_NoOwner_EmployeeRecipientType_RecipientUnresolved()
    {
        using var db = TestDbContext.CreateNew();
        var policy = CreateDefaultPolicy();
        db.EscalationPolicies.Add(policy);
        db.EscalationLevels.Add(CreateLevel(policy.Id, 1, EscalationRecipientType.Employee));
        var targetCase = CreateCase(ownerEmployeeId: null);
        db.Cases.Add(targetCase);
        db.Reminders.Add(CreateSentReminder(targetCase.Id));
        await db.SaveChangesAsync();

        var service = new EscalationService(db);
        var outcome = await service.EvaluateCaseAsync(targetCase.Id, CancellationToken.None);

        Assert.Equal(EscalationOutcome.RecipientUnresolved, outcome);
        var evt = await db.EscalationEvents.SingleAsync();
        Assert.Equal(EscalationOutcome.RecipientUnresolved, evt.Outcome);
        Assert.Null(evt.RecipientDisplay);
        // Recipient-unresolved must not be silently treated as a success — WorkStatus stays as-is.
        var reloadedCase = await db.Cases.AsNoTracking().SingleAsync();
        Assert.NotEqual(CaseWorkStatus.Escalated, reloadedCase.WorkStatus);
    }

    /// <summary>§59 "Employee's Supervisor" — resolves via Employee.SupervisorEmployeeId, using organizational data.</summary>
    [Fact]
    public async Task EvaluateCaseAsync_SupervisorRecipientType_ResolvesViaOrgData()
    {
        using var db = TestDbContext.CreateNew();
        var supervisor = new Employee { FullName = "Maria Santos", Email = "maria@sawo.com", IsActive = true };
        db.Employees.Add(supervisor);
        var owner = new Employee { FullName = "John Smith", Email = "john@sawo.com", IsActive = true, SupervisorEmployeeId = supervisor.Id };
        db.Employees.Add(owner);
        var policy = CreateDefaultPolicy();
        db.EscalationPolicies.Add(policy);
        db.EscalationLevels.Add(CreateLevel(policy.Id, 1, EscalationRecipientType.EmployeeSupervisor));
        var targetCase = CreateCase(ownerEmployeeId: owner.Id);
        db.Cases.Add(targetCase);
        db.Reminders.Add(CreateSentReminder(targetCase.Id));
        await db.SaveChangesAsync();

        var service = new EscalationService(db);
        var outcome = await service.EvaluateCaseAsync(targetCase.Id, CancellationToken.None);

        Assert.Equal(EscalationOutcome.Executed, outcome);
        var evt = await db.EscalationEvents.SingleAsync();
        Assert.Contains("Maria Santos", evt.RecipientDisplay);
    }

    /// <summary>§59 "Employee's Supervisor" with none configured — RecipientUnresolved, not a crash.</summary>
    [Fact]
    public async Task EvaluateCaseAsync_SupervisorRecipientType_NoSupervisorConfigured_RecipientUnresolved()
    {
        using var db = TestDbContext.CreateNew();
        var owner = new Employee { FullName = "John Smith", Email = "john@sawo.com", IsActive = true };
        db.Employees.Add(owner);
        var policy = CreateDefaultPolicy();
        db.EscalationPolicies.Add(policy);
        db.EscalationLevels.Add(CreateLevel(policy.Id, 1, EscalationRecipientType.EmployeeSupervisor));
        var targetCase = CreateCase(ownerEmployeeId: owner.Id);
        db.Cases.Add(targetCase);
        db.Reminders.Add(CreateSentReminder(targetCase.Id));
        await db.SaveChangesAsync();

        var service = new EscalationService(db);
        var outcome = await service.EvaluateCaseAsync(targetCase.Id, CancellationToken.None);

        Assert.Equal(EscalationOutcome.RecipientUnresolved, outcome);
    }

    /// <summary>§59 "Department Manager" — resolves via Employee.DepartmentId → Department.ManagerEmployeeId.</summary>
    [Fact]
    public async Task EvaluateCaseAsync_DepartmentManagerRecipientType_ResolvesViaOrgData()
    {
        using var db = TestDbContext.CreateNew();
        var manager = new Employee { FullName = "Department Head", Email = "head@sawo.com", IsActive = true };
        db.Employees.Add(manager);
        var department = new Department { Name = "Sales", ManagerEmployeeId = manager.Id };
        db.Departments.Add(department);
        var owner = new Employee { FullName = "John Smith", Email = "john@sawo.com", IsActive = true, DepartmentId = department.Id };
        db.Employees.Add(owner);
        var policy = CreateDefaultPolicy();
        db.EscalationPolicies.Add(policy);
        db.EscalationLevels.Add(CreateLevel(policy.Id, 1, EscalationRecipientType.DepartmentManager));
        var targetCase = CreateCase(ownerEmployeeId: owner.Id);
        db.Cases.Add(targetCase);
        db.Reminders.Add(CreateSentReminder(targetCase.Id));
        await db.SaveChangesAsync();

        var service = new EscalationService(db);
        var outcome = await service.EvaluateCaseAsync(targetCase.Id, CancellationToken.None);

        Assert.Equal(EscalationOutcome.Executed, outcome);
        var evt = await db.EscalationEvents.SingleAsync();
        Assert.Contains("Department Head", evt.RecipientDisplay);
    }

    /// <summary>§59 "Specific Employee" — resolves directly, independent of the Case's own owner/org chain.</summary>
    [Fact]
    public async Task EvaluateCaseAsync_SpecificEmployeeRecipientType_ResolvesDirectly()
    {
        using var db = TestDbContext.CreateNew();
        var specific = new Employee { FullName = "Escalation Contact", Email = "esc@sawo.com", IsActive = true };
        db.Employees.Add(specific);
        var policy = CreateDefaultPolicy();
        db.EscalationPolicies.Add(policy);
        db.EscalationLevels.Add(CreateLevel(policy.Id, 1, EscalationRecipientType.SpecificEmployee, specificEmployeeId: specific.Id));
        var targetCase = CreateCase(ownerEmployeeId: null); // no owner at all — proves this path doesn't need one
        db.Cases.Add(targetCase);
        db.Reminders.Add(CreateSentReminder(targetCase.Id));
        await db.SaveChangesAsync();

        var service = new EscalationService(db);
        var outcome = await service.EvaluateCaseAsync(targetCase.Id, CancellationToken.None);

        Assert.Equal(EscalationOutcome.Executed, outcome);
        var evt = await db.EscalationEvents.SingleAsync();
        Assert.Contains("Escalation Contact", evt.RecipientDisplay);
    }

    /// <summary>§59 "Specific Group" — resolves to all active members.</summary>
    [Fact]
    public async Task EvaluateCaseAsync_SpecificGroupRecipientType_ResolvesAllActiveMembers()
    {
        using var db = TestDbContext.CreateNew();
        var member1 = new Employee { FullName = "Member One", Email = "one@sawo.com", IsActive = true };
        var member2 = new Employee { FullName = "Member Two", Email = "two@sawo.com", IsActive = true };
        var inactiveMember = new Employee { FullName = "Inactive Member", Email = "gone@sawo.com", IsActive = false };
        db.Employees.AddRange(member1, member2, inactiveMember);
        var group = new EscalationGroup { Name = "Escalation Team" };
        db.EscalationGroups.Add(group);
        db.EscalationGroupMembers.Add(new EscalationGroupMember { EscalationGroupId = group.Id, EmployeeId = member1.Id });
        db.EscalationGroupMembers.Add(new EscalationGroupMember { EscalationGroupId = group.Id, EmployeeId = member2.Id });
        db.EscalationGroupMembers.Add(new EscalationGroupMember { EscalationGroupId = group.Id, EmployeeId = inactiveMember.Id });
        var policy = CreateDefaultPolicy();
        db.EscalationPolicies.Add(policy);
        db.EscalationLevels.Add(CreateLevel(policy.Id, 1, EscalationRecipientType.SpecificGroup, specificGroupId: group.Id));
        var targetCase = CreateCase();
        db.Cases.Add(targetCase);
        db.Reminders.Add(CreateSentReminder(targetCase.Id));
        await db.SaveChangesAsync();

        var service = new EscalationService(db);
        var outcome = await service.EvaluateCaseAsync(targetCase.Id, CancellationToken.None);

        Assert.Equal(EscalationOutcome.Executed, outcome);
        var evt = await db.EscalationEvents.SingleAsync();
        Assert.Contains("Member One", evt.RecipientDisplay);
        Assert.Contains("Member Two", evt.RecipientDisplay);
        Assert.DoesNotContain("Inactive Member", evt.RecipientDisplay);
    }

    /// <summary>§59 "Specific Group" with only inactive/no members — RecipientUnresolved.</summary>
    [Fact]
    public async Task EvaluateCaseAsync_SpecificGroupRecipientType_NoActiveMembers_RecipientUnresolved()
    {
        using var db = TestDbContext.CreateNew();
        var group = new EscalationGroup { Name = "Empty Team" };
        db.EscalationGroups.Add(group);
        var policy = CreateDefaultPolicy();
        db.EscalationPolicies.Add(policy);
        db.EscalationLevels.Add(CreateLevel(policy.Id, 1, EscalationRecipientType.SpecificGroup, specificGroupId: group.Id));
        var targetCase = CreateCase();
        db.Cases.Add(targetCase);
        db.Reminders.Add(CreateSentReminder(targetCase.Id));
        await db.SaveChangesAsync();

        var service = new EscalationService(db);
        var outcome = await service.EvaluateCaseAsync(targetCase.Id, CancellationToken.None);

        Assert.Equal(EscalationOutcome.RecipientUnresolved, outcome);
    }

    /// <summary>§64 — escalation must never mutate Case.OwnerEmployeeId, even across multiple levels.</summary>
    [Fact]
    public async Task EvaluateCaseAsync_MultipleLevels_NeverTransfersOwnership()
    {
        using var db = TestDbContext.CreateNew();
        var owner = new Employee { FullName = "John Smith", Email = "john@sawo.com", IsActive = true };
        var supervisor = new Employee { FullName = "Maria Santos", Email = "maria@sawo.com", IsActive = true };
        owner.SupervisorEmployeeId = supervisor.Id;
        db.Employees.AddRange(owner, supervisor);
        var policy = CreateDefaultPolicy();
        db.EscalationPolicies.Add(policy);
        db.EscalationLevels.Add(CreateLevel(policy.Id, 1, EscalationRecipientType.Employee));
        db.EscalationLevels.Add(CreateLevel(policy.Id, 2, EscalationRecipientType.EmployeeSupervisor));
        var targetCase = CreateCase(ownerEmployeeId: owner.Id);
        db.Cases.Add(targetCase);
        db.Reminders.Add(CreateSentReminder(targetCase.Id));
        await db.SaveChangesAsync();

        var service = new EscalationService(db);
        await service.EvaluateCaseAsync(targetCase.Id, CancellationToken.None);
        await service.EvaluateCaseAsync(targetCase.Id, CancellationToken.None);

        var reloadedCase = await db.Cases.AsNoTracking().SingleAsync();
        Assert.Equal(owner.Id, reloadedCase.OwnerEmployeeId); // still John, never reassigned to Maria.
    }

    /// <summary>§43/§46 boundary — ALREADY_REPLIED remains an employee claim; this service reads only Case.ReplyStatus (the verified fact), never any claim/event data, so a claim alone cannot prevent or cause escalation.</summary>
    [Fact]
    public async Task EvaluateCaseAsync_ReadsOnlyVerifiedReplyStatus_NotClaims()
    {
        using var db = TestDbContext.CreateNew();
        var owner = new Employee { FullName = "John Smith", Email = "john@sawo.com", IsActive = true };
        db.Employees.Add(owner);
        var policy = CreateDefaultPolicy();
        db.EscalationPolicies.Add(policy);
        db.EscalationLevels.Add(CreateLevel(policy.Id, 1, EscalationRecipientType.Employee));
        // ReplyStatus is still AwaitingReply — an employee's ALREADY_REPLIED claim (Phase 7) would
        // never touch this field, so escalation must proceed exactly as if no claim was ever made.
        var targetCase = CreateCase(ownerEmployeeId: owner.Id, replyStatus: CaseReplyStatus.AwaitingReply);
        db.Cases.Add(targetCase);
        db.Reminders.Add(CreateSentReminder(targetCase.Id));
        await db.SaveChangesAsync();

        var service = new EscalationService(db);
        var outcome = await service.EvaluateCaseAsync(targetCase.Id, CancellationToken.None);

        Assert.Equal(EscalationOutcome.Executed, outcome);
    }

    /// <summary>§78 idempotency — running RunAsync twice in a row (simulating a Hangfire retry) must not create two Executed events for the same level.</summary>
    [Fact]
    public async Task RunAsync_CalledTwiceInARow_DoesNotDuplicateExecutedLevel()
    {
        using var db = TestDbContext.CreateNew();
        var owner = new Employee { FullName = "John Smith", Email = "john@sawo.com", IsActive = true };
        db.Employees.Add(owner);
        var policy = CreateDefaultPolicy(cooldown: TimeSpan.FromDays(1));
        db.EscalationPolicies.Add(policy);
        db.EscalationLevels.Add(CreateLevel(policy.Id, 1, EscalationRecipientType.Employee));
        var targetCase = CreateCase(ownerEmployeeId: owner.Id);
        db.Cases.Add(targetCase);
        db.Reminders.Add(CreateSentReminder(targetCase.Id));
        await db.SaveChangesAsync();

        var service = new EscalationService(db);
        await service.RunAsync(10, CancellationToken.None);
        await service.RunAsync(10, CancellationToken.None);

        Assert.Equal(1, await db.EscalationEvents.CountAsync(e => e.CaseId == targetCase.Id && e.Outcome == EscalationOutcome.Executed));
    }

    /// <summary>§63 — every Executed/RecipientUnresolved escalation appends a CaseEvent to the Case History timeline.</summary>
    [Fact]
    public async Task EvaluateCaseAsync_Executed_AppendsCaseEvent()
    {
        using var db = TestDbContext.CreateNew();
        var owner = new Employee { FullName = "John Smith", Email = "john@sawo.com", IsActive = true };
        db.Employees.Add(owner);
        var policy = CreateDefaultPolicy();
        db.EscalationPolicies.Add(policy);
        db.EscalationLevels.Add(CreateLevel(policy.Id, 1, EscalationRecipientType.Employee));
        var targetCase = CreateCase(ownerEmployeeId: owner.Id);
        db.Cases.Add(targetCase);
        db.Reminders.Add(CreateSentReminder(targetCase.Id));
        await db.SaveChangesAsync();

        var service = new EscalationService(db);
        await service.EvaluateCaseAsync(targetCase.Id, CancellationToken.None);

        var caseEvent = await db.CaseEvents.SingleAsync(e => e.CaseId == targetCase.Id);
        Assert.Equal(CaseEventType.EscalationEvent, caseEvent.EventType);
    }

    /// <summary>Skipped attempts are recorded as EscalationEvent rows (queryable) but deliberately do NOT pollute the Case History timeline.</summary>
    [Fact]
    public async Task EvaluateCaseAsync_Skipped_DoesNotAppendCaseEvent()
    {
        using var db = TestDbContext.CreateNew();
        db.EscalationPolicies.Add(CreateDefaultPolicy(triggerReminderCount: 5));
        var targetCase = CreateCase();
        db.Cases.Add(targetCase);
        db.Reminders.Add(CreateSentReminder(targetCase.Id));
        await db.SaveChangesAsync();

        var service = new EscalationService(db);
        await service.EvaluateCaseAsync(targetCase.Id, CancellationToken.None);

        Assert.Equal(0, await db.CaseEvents.CountAsync(e => e.CaseId == targetCase.Id));
        Assert.Equal(1, await db.EscalationEvents.CountAsync(e => e.CaseId == targetCase.Id));
    }

    /// <summary>§57 policy resolution scoped to a Classification Profile/Category/Priority takes precedence over the default policy.</summary>
    [Fact]
    public async Task EvaluateCaseAsync_ProfileScopedPolicy_TakesPrecedenceOverDefault()
    {
        using var db = TestDbContext.CreateNew();
        var profileId = Guid.NewGuid();
        var owner = new Employee { FullName = "John Smith", Email = "john@sawo.com", IsActive = true };
        db.Employees.Add(owner);

        var defaultPolicy = CreateDefaultPolicy(triggerReminderCount: 10); // would never trigger with 1 reminder
        db.EscalationPolicies.Add(defaultPolicy);

        var scopedPolicy = new EscalationPolicy
        {
            Name = "VIP", Enabled = true, IsDefault = false, ClassificationProfileId = profileId,
            TriggerReminderCount = 1, GracePeriod = TimeSpan.Zero, Cooldown = TimeSpan.Zero, MaximumLevel = 3, Channel = "Email",
        };
        db.EscalationPolicies.Add(scopedPolicy);
        db.EscalationLevels.Add(CreateLevel(scopedPolicy.Id, 1, EscalationRecipientType.Employee));

        var account = new EmailAccount
        {
            EmailAddress = "vip@sawo.com", Purpose = EmailAccountPurpose.Inbound, Protocol = EmailProtocol.Imap,
            Host = "imap.example.com", Port = 993, Username = "vip@sawo.com", AuthMethod = EmailAuthMethod.Password,
        };
        db.EmailAccounts.Add(account);

        var targetCase = CreateCase(ownerEmployeeId: owner.Id);
        targetCase.EmailAccountId = account.Id;
        db.Cases.Add(targetCase);
        db.Reminders.Add(CreateSentReminder(targetCase.Id));

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
            EmailMessageId = message.Id, Decision = ImportanceDecision.Important, ClassificationProfileId = profileId,
        });
        await db.SaveChangesAsync();

        var service = new EscalationService(db);
        var outcome = await service.EvaluateCaseAsync(targetCase.Id, CancellationToken.None);

        Assert.Equal(EscalationOutcome.Executed, outcome);
        var evt = await db.EscalationEvents.SingleAsync(e => e.Outcome == EscalationOutcome.Executed);
        Assert.Equal(scopedPolicy.Id, evt.EscalationPolicyId);
    }

    /// <summary>Test Policy (§57 "Test Policy") — dry-run must not write any EscalationEvent.</summary>
    [Fact]
    public async Task TestPolicyAsync_EligibleCase_ReturnsWouldEscalate_WritesNoEvent()
    {
        using var db = TestDbContext.CreateNew();
        var owner = new Employee { FullName = "John Smith", Email = "john@sawo.com", IsActive = true };
        db.Employees.Add(owner);
        var policy = CreateDefaultPolicy();
        db.EscalationPolicies.Add(policy);
        db.EscalationLevels.Add(CreateLevel(policy.Id, 1, EscalationRecipientType.Employee));
        var targetCase = CreateCase(ownerEmployeeId: owner.Id);
        db.Cases.Add(targetCase);
        db.Reminders.Add(CreateSentReminder(targetCase.Id));
        await db.SaveChangesAsync();

        var service = new EscalationService(db);
        var result = await service.TestPolicyAsync(policy.Id, targetCase.Id, CancellationToken.None);

        Assert.True(result.WouldEscalate);
        Assert.Equal(1, result.EligibleLevel);
        Assert.Contains("John Smith", result.RecipientDisplay);
        Assert.Equal(0, await db.EscalationEvents.CountAsync());
    }

    /// <summary>RunAsync — Cases whose reminders never Sent (still Scheduled) are not candidates at all.</summary>
    [Fact]
    public async Task RunAsync_NoSentReminders_NotACandidate()
    {
        using var db = TestDbContext.CreateNew();
        db.EscalationPolicies.Add(CreateDefaultPolicy());
        var targetCase = CreateCase();
        db.Cases.Add(targetCase);
        db.Reminders.Add(new Reminder
        {
            CaseId = targetCase.Id, Trigger = ReminderTrigger.InitialActionRequired, Status = ReminderStatus.Scheduled,
            SequenceNumber = 1, ScheduledForUtc = DateTimeOffset.UtcNow.AddHours(2),
        });
        await db.SaveChangesAsync();

        var service = new EscalationService(db);
        var result = await service.RunAsync(10, CancellationToken.None);

        Assert.Equal(0, result.Considered);
    }
}
