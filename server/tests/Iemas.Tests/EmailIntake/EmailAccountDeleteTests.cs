using Iemas.Application.Common.Providers;
using Iemas.Application.EmailAccounts;
using Iemas.Domain.Email;
using Iemas.Infrastructure.Providers;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Tests.EmailIntake;

public class EmailAccountDeleteTests
{
    private static EmailAccountService CreateService(TestDbContext db, NoOpAuditService audit) =>
        new(db, new PassThroughEncryptionService(), new EmailProviderAdapterResolver(Array.Empty<IEmailProviderAdapter>()), audit);

    private static EmailAccount AddAccount(TestDbContext db, bool isActive)
    {
        var account = new EmailAccount { EmailAddress = "old@sawo.com", IsActive = isActive, Purpose = EmailAccountPurpose.Inbound };
        db.EmailAccounts.Add(account);
        return account;
    }

    [Fact]
    public async Task DeleteAsync_InactiveWithoutEmail_RemovesAccountCredentialSyncStateAndLogs()
    {
        using var db = TestDbContext.CreateNew();
        var account = AddAccount(db, isActive: false);
        db.EmailCredentials.Add(new EmailCredential { EmailAccountId = account.Id });
        db.EmailSyncStates.Add(new EmailSyncState { EmailAccountId = account.Id });
        db.EmailIntakeLogs.Add(new EmailIntakeLog { EmailAccountId = account.Id, Detail = "LOGIN failed" });
        await db.SaveChangesAsync();
        var audit = new NoOpAuditService();

        var result = await CreateService(db, audit).DeleteAsync(account.Id, CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.False(await db.EmailAccounts.AnyAsync());
        Assert.False(await db.EmailCredentials.AnyAsync());
        Assert.False(await db.EmailSyncStates.AnyAsync());
        Assert.False(await db.EmailIntakeLogs.AnyAsync());
        Assert.Equal("EMAIL_ACCOUNT_DELETED", Assert.Single(audit.Entries).Action);
    }

    [Fact]
    public async Task DeleteAsync_ActiveAccount_IsRejected()
    {
        using var db = TestDbContext.CreateNew();
        var account = AddAccount(db, isActive: true);
        await db.SaveChangesAsync();

        var result = await CreateService(db, new NoOpAuditService()).DeleteAsync(account.Id, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Deactivate", result.Error);
    }

    [Fact]
    public async Task DeleteAsync_HasStoredEmail_IsKept()
    {
        using var db = TestDbContext.CreateNew();
        var account = AddAccount(db, isActive: false);
        db.EmailMessages.Add(new EmailMessage { EmailAccountId = account.Id, ProviderMessageId = "1", FromAddress = "c@example.com", Subject = "s" });
        await db.SaveChangesAsync();

        var result = await CreateService(db, new NoOpAuditService()).DeleteAsync(account.Id, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("1 stored email(s)", result.Error);
        Assert.True(await db.EmailAccounts.AnyAsync());
    }
}
