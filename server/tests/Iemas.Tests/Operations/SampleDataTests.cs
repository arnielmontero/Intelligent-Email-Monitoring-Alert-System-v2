using Iemas.Application.Cases;
using Iemas.Application.Operations;
using Iemas.Application.Reminders;
using Iemas.Domain.Email;
using Iemas.Domain.Identity;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iemas.Tests.Operations;

public class SampleDataTests
{
    private static async Task<EmailAccount> SeedMailboxAsync(TestDbContext db, bool withOwner = true, DateTimeOffset? cutoff = null)
    {
        var owner = new Employee { FullName = "Arniel Montero", Email = "arniel.montero@sawo.com" };
        db.Employees.Add(owner);
        var account = new EmailAccount
        {
            EmailAddress = "sales@sawo.com", Purpose = EmailAccountPurpose.Inbound, Protocol = EmailProtocol.Imap,
            Host = "imap.example.com", Port = 993, Username = "sales@sawo.com", AuthMethod = EmailAuthMethod.Password,
            IsActive = true, MonitoringEnabled = true, OwnerEmployeeId = withOwner ? owner.Id : null,
            ProcessEmailsReceivedAfter = cutoff,
        };
        db.EmailAccounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    [Fact]
    public async Task Generate_WithoutAi_BecomesCases_AndNotWorkSamplesDoNot()
    {
        using var db = TestDbContext.CreateNew();
        // A cut-off set "now" (as after a reset) must not stop the samples from becoming Cases.
        var account = await SeedMailboxAsync(db, cutoff: DateTimeOffset.UtcNow);
        var service = new SampleDataService(db, new NoOpAuditService());

        var (result, error) = await service.GenerateAsync(new GenerateSampleDataRequest(account.Id, 10, UseAi: false), CancellationToken.None);

        Assert.Null(error);
        Assert.Equal(10, result!.Emails.Count);
        Assert.All(await db.EmailMessages.ToListAsync(), m => Assert.StartsWith(SampleDataService.SubjectPrefix, m.Subject));
        Assert.All(await db.EmailMessages.ToListAsync(), m => Assert.True(m.ReceivedAt >= account.ProcessEmailsReceivedAfter));

        var run = await new CaseWorkflowService(db, new CaseMatchingService(db), new ReminderSchedulingService(db)).RunAsync(100, CancellationToken.None);

        // 8 need a reply (one is a follow-up to another, so at most 8 Cases); the newsletter and auto-reply make none.
        Assert.Equal(8, run.ConsideredCount);
        Assert.InRange(await db.Cases.CountAsync(), 7, 8);
        Assert.All(await db.Cases.ToListAsync(), c => Assert.Equal(account.OwnerEmployeeId, c.OwnerEmployeeId));
    }

    [Fact]
    public async Task Generate_WithAi_LeavesEmailsForTheClassifier()
    {
        using var db = TestDbContext.CreateNew();
        var account = await SeedMailboxAsync(db);
        var service = new SampleDataService(db, new NoOpAuditService());

        await service.GenerateAsync(new GenerateSampleDataRequest(account.Id, 3, UseAi: true), CancellationToken.None);

        Assert.All(await db.EmailMessages.ToListAsync(), m => Assert.Equal(EmailProcessingStatus.PendingClassification, m.ProcessingStatus));
        Assert.Equal(0, await db.EmailClassifications.CountAsync());
    }

    [Fact]
    public async Task Generate_MailboxWithoutOwner_IsRefused()
    {
        using var db = TestDbContext.CreateNew();
        var account = await SeedMailboxAsync(db, withOwner: false);
        var (result, error) = await new SampleDataService(db, new NoOpAuditService())
            .GenerateAsync(new GenerateSampleDataRequest(account.Id, 5, false), CancellationToken.None);

        Assert.Null(result);
        Assert.Contains("owner", error);
        Assert.Equal(0, await db.EmailMessages.CountAsync());
    }
}
