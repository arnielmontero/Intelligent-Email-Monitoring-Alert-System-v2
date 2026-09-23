using Iemas.Api.HealthChecks;
using Iemas.Domain.Email;
using Iemas.Tests.TestSupport;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Iemas.Tests.HealthChecks;

public class ImapHealthCheckTests
{
    private static EmailAccount CreateMonitoredAccount(bool monitoringEnabled = true, bool isActive = true) => new()
    {
        EmailAddress = "inbox@example.com",
        Host = "imap.example.com",
        Port = 993,
        Protocol = EmailProtocol.Imap,
        Username = "inbox@example.com",
        AuthMethod = EmailAuthMethod.Password,
        Purpose = EmailAccountPurpose.Inbound,
        MonitoringEnabled = monitoringEnabled,
        IsActive = isActive,
    };

    [Fact]
    public async Task CheckHealthAsync_NoMonitoredMailboxes_ReturnsHealthy()
    {
        using var db = TestDbContext.CreateNew();
        var check = new ImapHealthCheck(db);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_AllMailboxesSyncingCleanly_ReturnsHealthy()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateMonitoredAccount();
        db.EmailAccounts.Add(account);
        db.EmailSyncStates.Add(new EmailSyncState { EmailAccountId = account.Id, ConsecutiveFailureCount = 0 });
        await db.SaveChangesAsync();
        var check = new ImapHealthCheck(db);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_OneMailboxWithFewFailures_ReturnsDegraded_NotUnhealthy()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateMonitoredAccount();
        db.EmailAccounts.Add(account);
        db.EmailSyncStates.Add(new EmailSyncState { EmailAccountId = account.Id, ConsecutiveFailureCount = 1, LastSyncError = "Transient timeout" });
        await db.SaveChangesAsync();
        var check = new ImapHealthCheck(db);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_MailboxAtFailureThreshold_ReturnsUnhealthy()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateMonitoredAccount();
        db.EmailAccounts.Add(account);
        db.EmailSyncStates.Add(new EmailSyncState { EmailAccountId = account.Id, ConsecutiveFailureCount = 3, LastSyncError = "Authentication failed" });
        await db.SaveChangesAsync();
        var check = new ImapHealthCheck(db);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("inbox@example.com", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_DisabledOrInactiveAccounts_AreExcludedFromTheCheck()
    {
        using var db = TestDbContext.CreateNew();
        var disabledAccount = CreateMonitoredAccount(monitoringEnabled: false);
        var inactiveAccount = CreateMonitoredAccount(isActive: false);
        db.EmailAccounts.AddRange(disabledAccount, inactiveAccount);
        db.EmailSyncStates.AddRange(
            new EmailSyncState { EmailAccountId = disabledAccount.Id, ConsecutiveFailureCount = 10 },
            new EmailSyncState { EmailAccountId = inactiveAccount.Id, ConsecutiveFailureCount = 10 });
        await db.SaveChangesAsync();
        var check = new ImapHealthCheck(db);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        // Neither account is monitored/active, so despite their high failure counts this must not
        // report Unhealthy — a disabled account failing repeatedly is not a live production problem.
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_MixOfHealthyAndFailingMailboxes_UnhealthyDescriptionListsOnlyTheFailingOnes()
    {
        using var db = TestDbContext.CreateNew();
        var healthyAccount = CreateMonitoredAccount();
        healthyAccount.EmailAddress = "healthy@example.com";
        var failingAccount = CreateMonitoredAccount();
        failingAccount.EmailAddress = "failing@example.com";
        db.EmailAccounts.AddRange(healthyAccount, failingAccount);
        db.EmailSyncStates.AddRange(
            new EmailSyncState { EmailAccountId = healthyAccount.Id, ConsecutiveFailureCount = 0 },
            new EmailSyncState { EmailAccountId = failingAccount.Id, ConsecutiveFailureCount = 5, LastSyncError = "Connection refused" });
        await db.SaveChangesAsync();
        var check = new ImapHealthCheck(db);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("failing@example.com", result.Description);
        Assert.DoesNotContain("healthy@example.com", result.Description);
    }
}
