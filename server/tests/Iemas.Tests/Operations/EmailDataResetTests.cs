using Iemas.Application.Operations;
using Iemas.Domain.Ai;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Iemas.Domain.Identity;
using Iemas.Domain.Operations;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iemas.Tests.Operations;

public class EmailDataResetTests
{
    private static async Task<(EmailAccount Account, EmailMessage Message)> SeedAsync(TestDbContext db)
    {
        var account = new EmailAccount
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
            ProcessEmailsReceivedAfter = DateTimeOffset.UtcNow.AddDays(-30),
        };
        db.EmailAccounts.Add(account);
        db.SystemSettings.Add(new SystemSetting { Key = "org.name", Value = "SAWO" });

        var theCase = new Case
        {
            CaseNumber = "CASE-000001",
            EmailAccountId = account.Id,
            CustomerEmailAddress = "customer@example.com",
            Subject = "Price Request",
            NormalizedSubject = "Price Request",
            WorkStatus = CaseWorkStatus.ActionRequired,
            ReplyStatus = CaseReplyStatus.AwaitingReply,
            FirstEmailReceivedAt = DateTimeOffset.UtcNow.AddDays(-1),
            LastActivityAt = DateTimeOffset.UtcNow.AddDays(-1),
        };
        db.Cases.Add(theCase);
        var message = new EmailMessage
        {
            EmailAccountId = account.Id,
            Provider = EmailProtocol.Imap,
            ProviderMessageId = "1",
            MessageId = "<original@example.com>",
            FromAddress = "customer@example.com",
            ToAddresses = "sales@sawo.com",
            Subject = "Price Request",
            ReceivedAt = DateTimeOffset.UtcNow.AddDays(-1),
            CaseId = theCase.Id,
        };
        db.EmailMessages.Add(message);
        await db.SaveChangesAsync();

        db.CaseEmails.Add(new CaseEmail { CaseId = theCase.Id, EmailMessageId = message.Id, MatchSignal = CaseMatchSignal.NewCase });
        db.CaseEvents.Add(new CaseEvent { CaseId = theCase.Id, EventType = CaseEventType.ReplyVerification, Detail = "checked" });
        db.AiUsageRecords.Add(new AiUsageRecord { Provider = "OpenRouter", ModelIdentifier = "m", Purpose = "Classification", EmailMessageId = message.Id, Succeeded = true, CostUsd = 0.01m });
        await db.SaveChangesAsync();
        return (account, message);
    }

    [Fact]
    public async Task Reset_WithoutConfirmationWord_ChangesNothing()
    {
        using var db = TestDbContext.CreateNew();
        await SeedAsync(db);
        var service = new EmailDataResetService(db, new NoOpAuditService(), new FakeCurrentUserService());

        Assert.Null(await service.ResetAsync("reset please", null, CancellationToken.None));
        Assert.Equal(1, await db.EmailMessages.CountAsync());
        Assert.Equal(1, await db.Cases.CountAsync());
    }

    [Fact]
    public async Task Reset_RemovesEmailData_KeepsConfigurationAndCostHistory_AndMovesCutoff()
    {
        using var db = TestDbContext.CreateNew();
        await SeedAsync(db);
        var service = new EmailDataResetService(db, new NoOpAuditService(), new FakeCurrentUserService());
        var before = DateTimeOffset.UtcNow;

        var preview = await service.PreviewAsync(CancellationToken.None);
        Assert.Equal(1, preview.Counts.Emails);
        Assert.Equal(1, preview.Counts.Cases);

        var result = await service.ResetAsync("RESET", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, result!.Removed.Emails);
        Assert.Equal(1, result.Removed.Cases);
        Assert.Equal(1, result.Removed.CaseEvents);
        Assert.Equal(0, await db.EmailMessages.CountAsync());
        Assert.Equal(0, await db.Cases.CountAsync());
        Assert.Equal(0, await db.CaseEmails.CountAsync());
        Assert.Equal(0, await db.CaseEvents.CountAsync());

        // Configuration stays; the mailbox only starts fresh from the reset time.
        var account = await db.EmailAccounts.SingleAsync();
        Assert.True(account.ProcessEmailsReceivedAfter >= before);
        Assert.Equal("SAWO", (await db.SystemSettings.SingleAsync()).Value);

        // Cost history stays, without the link to the deleted email.
        var usage = await db.AiUsageRecords.SingleAsync();
        Assert.Null(usage.EmailMessageId);
        Assert.Equal(0.01m, usage.CostUsd);

        Assert.Equal(0, (await service.PreviewAsync(CancellationToken.None)).Counts.Total);
    }

    [Fact]
    public async Task Reset_RemovesOnlyTickedTestRecords_AndClearsLinksFromRealRecords()
    {
        using var db = TestDbContext.CreateNew();
        var (realMailbox, _) = await SeedAsync(db);
        var testSupervisor = new Employee { FullName = "Live Verification Supervisor", Email = "lv.supervisor@sawo.com" };
        var testOwner = new Employee { FullName = "Pat O'Brien", Email = "pat.obrien@iemas.local" };
        var real = new Employee { FullName = "Arniel Montero", Email = "arniel.montero@sawo.com", SupervisorEmployeeId = testSupervisor.Id };
        db.Employees.AddRange(testSupervisor, testOwner, real);
        realMailbox.OwnerEmployeeId = real.Id;
        db.EmailAccounts.Add(new EmailAccount
        {
            EmailAddress = "testuser@localhost", Purpose = EmailAccountPurpose.Inbound, Protocol = EmailProtocol.Imap,
            Host = "greenmail-iemas", Port = 3993, Username = "testuser", AuthMethod = EmailAuthMethod.Password,
        });
        await db.SaveChangesAsync();
        var service = new EmailDataResetService(db, new NoOpAuditService(), new FakeCurrentUserService());

        var detected = (await service.PreviewAsync(CancellationToken.None)).TestRecords;
        Assert.Equal(new[] { "Live Verification Supervisor", "Pat O'Brien", "testuser@localhost" }, detected.Select(r => r.Name).OrderBy(n => n));

        // Tick the supervisor and the test mailbox, not Pat; a real id smuggled into the request is ignored.
        var ticked = detected.Where(r => r.Name != "Pat O'Brien").Select(r => r.Id).Append(real.Id).ToList();
        var result = await service.ResetAsync("RESET", ticked, CancellationToken.None);

        Assert.Equal(2, result!.RemovedTestRecords.Count);
        Assert.Equal(new[] { "Arniel Montero", "Pat O'Brien" }, (await db.Employees.Select(e => e.FullName).ToListAsync()).OrderBy(n => n));
        Assert.Null((await db.Employees.SingleAsync(e => e.Id == real.Id)).SupervisorEmployeeId);
        Assert.Equal("sales@sawo.com", (await db.EmailAccounts.SingleAsync()).EmailAddress);
    }
}
