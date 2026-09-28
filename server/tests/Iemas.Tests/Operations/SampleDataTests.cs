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
    private static async Task<EmailAccount> SeedRealMailboxAsync(TestDbContext db, DateTimeOffset? cutoff = null)
    {
        var owner = new Employee { FullName = "Arniel Montero", Email = "arniel.montero@sawo.com" };
        db.Employees.Add(owner);
        var account = new EmailAccount
        {
            EmailAddress = "arniel.montero@sawo.com", Purpose = EmailAccountPurpose.Inbound, Protocol = EmailProtocol.Imap,
            Host = "mail.sawo.com", Port = 993, Username = "arniel.montero@sawo.com", AuthMethod = EmailAuthMethod.Password,
            IsActive = true, MonitoringEnabled = true, OwnerEmployeeId = owner.Id, ProcessEmailsReceivedAfter = cutoff,
        };
        db.EmailAccounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    private static CaseWorkflowService Workflow(TestDbContext db) => new(db, new CaseMatchingService(db), new ReminderSchedulingService(db));

    [Fact]
    public async Task Generate_CreatesSampleTeam_Once_WithSupervisorChain()
    {
        using var db = TestDbContext.CreateNew();
        await SeedRealMailboxAsync(db);
        var service = new SampleDataService(db, new NoOpAuditService());

        var (first, _) = await service.GenerateAsync(new GenerateSampleDataRequest(3, false), CancellationToken.None);
        var (second, _) = await service.GenerateAsync(new GenerateSampleDataRequest(3, false), CancellationToken.None);

        Assert.NotEmpty(first!.TeamCreated);
        Assert.Empty(second!.TeamCreated);
        Assert.Equal(6, await db.Employees.CountAsync());            // Arniel + 5 sample people
        Assert.Equal(4, await db.EmailAccounts.CountAsync());        // the real mailbox + 3 sample mailboxes
        Assert.Equal(3, await db.Agents.CountAsync());
        // The database requires a unique registration token per PC (the in-memory test database doesn't enforce it).
        var tokens = await db.Agents.Select(a => a.RegistrationRequestToken).ToListAsync();
        Assert.All(tokens, t => Assert.False(string.IsNullOrEmpty(t)));
        Assert.Equal(tokens.Count, tokens.Distinct().Count());
        var liam = await db.Employees.SingleAsync(e => e.Email == $"liam.andersson@{SampleDataService.SampleDomain}");
        var mark = await db.Employees.SingleAsync(e => e.Id == liam.SupervisorEmployeeId);
        Assert.Equal("Mark Evans", mark.FullName);
        Assert.NotNull((await db.Departments.SingleAsync()).ManagerEmployeeId);
        Assert.All(await db.EmailAccounts.Where(a => a.Host == SampleDataService.SampleMailboxHost).ToListAsync(), a => Assert.False(a.MonitoringEnabled));
    }

    [Fact]
    public async Task Generate_WithoutAi_BecomesCases_ForSeveralOwners_AndNotWorkSamplesDoNot()
    {
        using var db = TestDbContext.CreateNew();
        // A cut-off set "now" (as after a reset) must not stop the samples from becoming Cases.
        var account = await SeedRealMailboxAsync(db, cutoff: DateTimeOffset.UtcNow);
        var service = new SampleDataService(db, new NoOpAuditService(), new Random(7));

        var (result, error) = await service.GenerateAsync(new GenerateSampleDataRequest(20, UseAi: false), CancellationToken.None);

        Assert.Null(error);
        Assert.Equal(20, result!.Emails.Count);
        var messages = await db.EmailMessages.Include(m => m.EmailAccount).ToListAsync();
        Assert.All(messages, m => Assert.StartsWith(SampleDataService.SubjectPrefix, m.Subject));
        Assert.All(messages, m => Assert.True(m.EmailAccount.ProcessEmailsReceivedAfter is null || m.ReceivedAt >= m.EmailAccount.ProcessEmailsReceivedAfter));
        Assert.True(messages.Select(m => m.FromAddress[(m.FromAddress.IndexOf('@') + 1)..]).Distinct().Count() > 1);
        Assert.DoesNotContain(messages, m => m.FromAddress.EndsWith("@sawo.com"));
        Assert.True(messages.Select(m => m.EmailAccountId).Distinct().Count() > 1);

        var run = await Workflow(db).RunAsync(100, CancellationToken.None);

        var needReply = result.Emails.Count(e => e.Expected != "Not work — no Case");
        Assert.Equal(needReply, run.ConsideredCount);
        Assert.True(await db.Cases.Select(c => c.OwnerEmployeeId).Distinct().CountAsync() > 1);
    }

    [Fact]
    public async Task Generate_WithAi_LeavesEmailsForTheClassifier()
    {
        using var db = TestDbContext.CreateNew();
        await SeedRealMailboxAsync(db);
        var service = new SampleDataService(db, new NoOpAuditService());

        await service.GenerateAsync(new GenerateSampleDataRequest(3, UseAi: true), CancellationToken.None);

        Assert.All(await db.EmailMessages.ToListAsync(), m => Assert.Equal(EmailProcessingStatus.PendingClassification, m.ProcessingStatus));
        Assert.Equal(0, await db.EmailClassifications.CountAsync());
    }

    [Fact]
    public async Task Generate_FollowUpAlwaysRepliesToAnEarlierQuotation()
    {
        for (var seed = 0; seed < 30; seed++)
        {
            using var db = TestDbContext.CreateNew();
            await SeedRealMailboxAsync(db);
            var (result, _) = await new SampleDataService(db, new NoOpAuditService(), new Random(seed))
                .GenerateAsync(new GenerateSampleDataRequest(20, false), CancellationToken.None);

            var messages = await db.EmailMessages.ToListAsync();
            foreach (var followUp in messages.Where(m => m.InReplyTo != null))
            {
                var original = messages.Single(m => m.MessageId == followUp.InReplyTo);
                Assert.Equal(original.FromAddress, followUp.FromAddress);
                Assert.Equal(original.EmailAccountId, followUp.EmailAccountId);
                Assert.True(original.ReceivedAt < followUp.ReceivedAt);
            }
            Assert.Equal(20, result!.Emails.Count);
        }
    }

    [Fact]
    public async Task Reset_RemovesTheWholeSampleTeam_AndKeepsTheRealMailboxAndOwner()
    {
        using var db = TestDbContext.CreateNew();
        var real = await SeedRealMailboxAsync(db);
        await new SampleDataService(db, new NoOpAuditService()).GenerateAsync(new GenerateSampleDataRequest(10, false), CancellationToken.None);
        await Workflow(db).RunAsync(100, CancellationToken.None);

        var reset = new EmailDataResetService(db, new NoOpAuditService(), new FakeCurrentUserService());
        var detected = (await reset.PreviewAsync(CancellationToken.None)).TestRecords;
        Assert.Equal(12, detected.Count); // 5 employees, 3 mailboxes, 3 PCs, 1 department
        Assert.DoesNotContain(detected, r => r.Name.Contains("arniel", StringComparison.OrdinalIgnoreCase));

        await reset.ResetAsync("RESET", detected.Select(r => r.Id).ToList(), CancellationToken.None);

        Assert.Equal("Arniel Montero", (await db.Employees.SingleAsync()).FullName);
        Assert.Equal(real.Id, (await db.EmailAccounts.SingleAsync()).Id);
        Assert.Equal(0, await db.Agents.CountAsync());
        Assert.Equal(0, await db.Departments.CountAsync());
        Assert.Equal(0, await db.EmailMessages.CountAsync());
        Assert.Equal(0, await db.Cases.CountAsync());
    }
}
