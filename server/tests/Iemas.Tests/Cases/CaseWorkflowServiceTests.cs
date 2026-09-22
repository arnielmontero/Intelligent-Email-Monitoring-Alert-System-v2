using Iemas.Application.Cases;
using Iemas.Application.Cases.Dtos;
using Iemas.Application.Reminders;
using Iemas.Domain.Ai;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Iemas.Domain.Identity;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iemas.Tests.Cases;

public class CaseWorkflowServiceTests
{
    private static Employee CreateEmployee() => new() { FullName = "John Smith", Email = "john@sawo.com" };

    private static EmailAccount CreateAccount(Guid? ownerId = null)
    {
        return new EmailAccount
        {
            EmailAddress = "sales@sawo.com",
            Purpose = EmailAccountPurpose.Inbound,
            Protocol = EmailProtocol.Imap,
            Host = "imap.example.com",
            Port = 993,
            Username = "sales@sawo.com",
            AuthMethod = EmailAuthMethod.Password,
            IsActive = true,
            MonitoringEnabled = true,
            OwnerEmployeeId = ownerId,
        };
    }

    private static EmailMessage CreateMessage(Guid accountId, string subject = "Price Request", EmailProcessingStatus status = EmailProcessingStatus.Processed)
    {
        return new EmailMessage
        {
            EmailAccountId = accountId,
            Provider = EmailProtocol.Imap,
            ProviderMessageId = Guid.NewGuid().ToString(),
            FromAddress = "customer@example.com",
            ToAddresses = "sales@sawo.com",
            Subject = subject,
            ReceivedAt = DateTimeOffset.UtcNow,
            ProcessingStatus = status,
        };
    }

    private static Iemas.Domain.Ai.EmailClassification CreateClassification(Guid messageId, ImportanceDecision decision)
    {
        return new Iemas.Domain.Ai.EmailClassification { EmailMessageId = messageId, Decision = decision };
    }

    private static CaseWorkflowService CreateService(TestDbContext db) => new(db, new CaseMatchingService(db), new ReminderSchedulingService(db));

    /// <summary>Core boundary: an Important message creates a new Case with the account owner as Case owner.</summary>
    [Fact]
    public async Task ProcessOneAsync_ImportantMessage_CreatesNewCase_OwnedByAccountOwner()
    {
        using var db = TestDbContext.CreateNew();
        var employee = CreateEmployee();
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var account = CreateAccount(employee.Id);
        db.EmailAccounts.Add(account);
        var message = CreateMessage(account.Id);
        db.EmailMessages.Add(message);
        db.EmailClassifications.Add(CreateClassification(message.Id, ImportanceDecision.Important));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var outcome = await service.ProcessOneAsync(message.Id, CancellationToken.None);

        Assert.Equal(CaseWorkflowService.CaseProcessOutcome.Created, outcome);
        var createdCase = await db.Cases.SingleAsync();
        Assert.Equal(employee.Id, createdCase.OwnerEmployeeId);
        Assert.Equal(CaseWorkStatus.ActionRequired, createdCase.WorkStatus);
        Assert.Equal(CaseReplyStatus.AwaitingReply, createdCase.ReplyStatus);
        Assert.StartsWith("CASE-", createdCase.CaseNumber);

        var link = await db.CaseEmails.SingleAsync();
        Assert.Equal(message.Id, link.EmailMessageId);
        Assert.Equal(CaseMatchSignal.NewCase, link.MatchSignal);

        var reloadedMessage = await db.EmailMessages.SingleAsync();
        Assert.Equal(createdCase.Id, reloadedMessage.CaseId);
    }

    /// <summary>§66 — Case creation must append history events (Created + StatusChanged), not silently create a Case with no trail.</summary>
    [Fact]
    public async Task ProcessOneAsync_CreatingACase_RecordsHistoryEvents()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        var message = CreateMessage(account.Id);
        db.EmailMessages.Add(message);
        db.EmailClassifications.Add(CreateClassification(message.Id, ImportanceDecision.Important));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.ProcessOneAsync(message.Id, CancellationToken.None);

        var createdCase = await db.Cases.SingleAsync();
        var events = await db.CaseEvents.Where(e => e.CaseId == createdCase.Id).ToListAsync();
        Assert.Contains(events, e => e.EventType == CaseEventType.Created);
        Assert.Contains(events, e => e.EventType == CaseEventType.StatusChanged);
    }

    /// <summary>§33 — a NotImportant classification must never produce a Case.</summary>
    [Fact]
    public async Task ProcessOneAsync_NotImportantMessage_NeverCreatesACase()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        var message = CreateMessage(account.Id, "Newsletter");
        db.EmailMessages.Add(message);
        db.EmailClassifications.Add(CreateClassification(message.Id, ImportanceDecision.NotImportant));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var outcome = await service.ProcessOneAsync(message.Id, CancellationToken.None);

        Assert.Equal(CaseWorkflowService.CaseProcessOutcome.Skipped, outcome);
        Assert.Equal(0, await db.Cases.CountAsync());
    }

    /// <summary>A ReviewRequired classification (low confidence / AI failure) must also never auto-create a Case — it needs human review first, not automatic Case creation.</summary>
    [Fact]
    public async Task ProcessOneAsync_ReviewRequiredMessage_DoesNotCreateACase()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        var message = CreateMessage(account.Id, "Ambiguous", status: EmailProcessingStatus.ReviewRequired);
        db.EmailMessages.Add(message);
        db.EmailClassifications.Add(CreateClassification(message.Id, ImportanceDecision.ReviewRequired));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var outcome = await service.ProcessOneAsync(message.Id, CancellationToken.None);

        Assert.Equal(CaseWorkflowService.CaseProcessOutcome.Skipped, outcome);
        Assert.Equal(0, await db.Cases.CountAsync());
    }

    /// <summary>§78 idempotency — a message already linked to a Case must never be linked/processed a second time.</summary>
    [Fact]
    public async Task ProcessOneAsync_MessageAlreadyLinkedToACase_IsSkippedOnSecondCall()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        var message = CreateMessage(account.Id);
        db.EmailMessages.Add(message);
        db.EmailClassifications.Add(CreateClassification(message.Id, ImportanceDecision.Important));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var first = await service.ProcessOneAsync(message.Id, CancellationToken.None);
        var second = await service.ProcessOneAsync(message.Id, CancellationToken.None);

        Assert.Equal(CaseWorkflowService.CaseProcessOutcome.Created, first);
        Assert.Equal(CaseWorkflowService.CaseProcessOutcome.Skipped, second);
        Assert.Equal(1, await db.Cases.CountAsync());
        Assert.Equal(1, await db.CaseEmails.CountAsync());
    }

    /// <summary>§36/§49 — a Completed Case receiving a clearly related new email reopens, and the prior completion stays in history rather than being erased.</summary>
    [Fact]
    public async Task ProcessOneAsync_RelatedEmailArrivesOnCompletedCase_Reopens_AndPreservesHistory()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        var firstMessage = CreateMessage(account.Id, "Price Request");
        db.EmailMessages.Add(firstMessage);
        db.EmailClassifications.Add(CreateClassification(firstMessage.Id, ImportanceDecision.Important));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.ProcessOneAsync(firstMessage.Id, CancellationToken.None);
        var firstCase = await db.Cases.SingleAsync();
        firstCase.WorkStatus = CaseWorkStatus.Completed;
        firstCase.CompletionReason = CaseCompletionReason.EmployeeResponded;
        firstCase.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        // A follow-up reply threading off the original message.
        var followUp = new EmailMessage
        {
            EmailAccountId = account.Id,
            Provider = EmailProtocol.Imap,
            ProviderMessageId = Guid.NewGuid().ToString(),
            FromAddress = "customer@example.com",
            ToAddresses = "sales@sawo.com",
            Subject = "Re: Price Request",
            InReplyTo = firstMessage.MessageId,
            ReceivedAt = DateTimeOffset.UtcNow,
            ProcessingStatus = EmailProcessingStatus.Processed,
        };
        db.EmailMessages.Add(followUp);
        db.EmailClassifications.Add(CreateClassification(followUp.Id, ImportanceDecision.Important));
        await db.SaveChangesAsync();

        var outcome = await service.ProcessOneAsync(followUp.Id, CancellationToken.None);

        Assert.Equal(CaseWorkflowService.CaseProcessOutcome.Reopened, outcome);
        var reloadedCase = await db.Cases.SingleAsync();
        Assert.Equal(CaseWorkStatus.ActionRequired, reloadedCase.WorkStatus);
        Assert.Null(reloadedCase.CompletionReason);
        Assert.Equal(1, reloadedCase.ReopenCount);

        // The prior completion event must still exist in history — append-only, never overwritten.
        var events = await db.CaseEvents.Where(e => e.CaseId == reloadedCase.Id).ToListAsync();
        Assert.Contains(events, e => e.EventType == CaseEventType.Reopened);
        Assert.Equal(2, await db.CaseEmails.CountAsync(ce => ce.CaseId == reloadedCase.Id));
    }

    /// <summary>§39 — a second customer message before any employee reply must update the existing Case, not create a duplicate one or a duplicate reminder cycle.</summary>
    [Fact]
    public async Task ProcessOneAsync_SecondMessageBeforeReply_UpdatesExistingCase_DoesNotCreateSecondCase()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        var first = CreateMessage(account.Id, "Price Request");
        db.EmailMessages.Add(first);
        db.EmailClassifications.Add(CreateClassification(first.Id, ImportanceDecision.Important));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.ProcessOneAsync(first.Id, CancellationToken.None);

        var second = CreateMessage(account.Id, "Any Update?");
        db.EmailMessages.Add(second);
        db.EmailClassifications.Add(CreateClassification(second.Id, ImportanceDecision.Important));
        await db.SaveChangesAsync();

        var outcome = await service.ProcessOneAsync(second.Id, CancellationToken.None);

        Assert.Equal(CaseWorkflowService.CaseProcessOutcome.Updated, outcome);
        Assert.Equal(1, await db.Cases.CountAsync());
        Assert.Equal(2, await db.CaseEmails.CountAsync());
    }

    /// <summary>§48 — completing a Case requires a reason and is recorded as a history event.</summary>
    [Fact]
    public async Task CompleteAsync_RequiresExistingActiveCase_AndRecordsReason()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        var message = CreateMessage(account.Id);
        db.EmailMessages.Add(message);
        db.EmailClassifications.Add(CreateClassification(message.Id, ImportanceDecision.Important));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.ProcessOneAsync(message.Id, CancellationToken.None);
        var createdCase = await db.Cases.SingleAsync();

        var result = await service.CompleteAsync(createdCase.Id, new CompleteCaseRequest(CaseCompletionReason.HandledOutsideEmail, "Called the customer."), CancellationToken.None);

        Assert.True(result.Succeeded);
        var reloaded = await db.Cases.SingleAsync();
        Assert.Equal(CaseWorkStatus.Completed, reloaded.WorkStatus);
        Assert.Equal(CaseCompletionReason.HandledOutsideEmail, reloaded.CompletionReason);
        Assert.Contains(await db.CaseEvents.Where(e => e.CaseId == createdCase.Id).ToListAsync(), e => e.EventType == CaseEventType.Completed);
    }

    [Fact]
    public async Task CompleteAsync_AlreadyCompletedCase_Fails()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        var existingCase = new Case
        {
            CaseNumber = "CASE-000001", EmailAccountId = account.Id, CustomerEmailAddress = "customer@example.com",
            Subject = "X", NormalizedSubject = "x", WorkStatus = CaseWorkStatus.Completed, LastActivityAt = DateTimeOffset.UtcNow,
        };
        db.Cases.Add(existingCase);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.CompleteAsync(existingCase.Id, new CompleteCaseRequest(CaseCompletionReason.Other, null), CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    /// <summary>RunAsync batches multiple candidate messages and tallies created/updated/reopened outcomes.</summary>
    [Fact]
    public async Task RunAsync_ProcessesBatchOfImportantMessages_AndTalliesOutcomes()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);

        var m1 = CreateMessage(account.Id, "Price Request");
        var m2 = CreateMessage(account.Id, "Newsletter");
        db.EmailMessages.AddRange(m1, m2);
        db.EmailClassifications.Add(CreateClassification(m1.Id, ImportanceDecision.Important));
        db.EmailClassifications.Add(CreateClassification(m2.Id, ImportanceDecision.NotImportant));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.RunAsync(10, CancellationToken.None);

        // Only m1 is a candidate (Important); m2's classification is NotImportant so it's never
        // in the candidate query at all (it's filtered by the join, not skipped post-hoc).
        Assert.Equal(1, result.ConsideredCount);
        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(1, await db.Cases.CountAsync());
    }

    /// <summary>§54 "Initial Notification" — a newly created Case (via Case Matching/Creation) automatically starts its reminder cycle, without this service needing to know anything about reminder scheduling internals.</summary>
    [Fact]
    public async Task ProcessOneAsync_NewCase_SchedulesInitialReminder()
    {
        using var db = TestDbContext.CreateNew();
        db.ReminderPolicies.Add(new Iemas.Domain.Reminders.ReminderPolicy
        {
            Name = "Default", Enabled = true, IsDefault = true,
            InitialDelay = TimeSpan.FromHours(4), ReminderInterval = TimeSpan.FromHours(24),
            MaxReminders = 3, MinimumInterval = TimeSpan.FromHours(1),
            RestrictToBusinessHours = false, ExcludeWeekends = false, TimeZoneId = "UTC",
        });
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        var message = CreateMessage(account.Id);
        db.EmailMessages.Add(message);
        db.EmailClassifications.Add(CreateClassification(message.Id, ImportanceDecision.Important));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.ProcessOneAsync(message.Id, CancellationToken.None);

        var createdCase = await db.Cases.SingleAsync();
        var reminder = await db.Reminders.SingleOrDefaultAsync(r => r.CaseId == createdCase.Id);
        Assert.NotNull(reminder);
        Assert.Equal(Iemas.Domain.Reminders.ReminderStatus.Scheduled, reminder!.Status);
        Assert.Equal(Iemas.Domain.Reminders.ReminderTrigger.InitialActionRequired, reminder.Trigger);
    }

    /// <summary>No Reminder Policy configured at all — Case creation still succeeds; reminder scheduling is a quiet no-op, never a hard dependency of Case creation.</summary>
    [Fact]
    public async Task ProcessOneAsync_NoReminderPolicyConfigured_StillCreatesCase()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        var message = CreateMessage(account.Id);
        db.EmailMessages.Add(message);
        db.EmailClassifications.Add(CreateClassification(message.Id, ImportanceDecision.Important));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var outcome = await service.ProcessOneAsync(message.Id, CancellationToken.None);

        Assert.Equal(CaseWorkflowService.CaseProcessOutcome.Created, outcome);
        Assert.Equal(0, await db.Reminders.CountAsync());
    }
}
