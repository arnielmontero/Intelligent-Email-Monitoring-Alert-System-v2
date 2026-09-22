using Iemas.Application.Common.Providers;
using Iemas.Application.EmailIntake;
using Iemas.Domain.Email;
using Iemas.Domain.Identity;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iemas.Tests.EmailIntake;

public class EmailIntakeServiceTests
{
    private static EmailAccount CreateAccount()
    {
        return new EmailAccount
        {
            EmailAddress = "sales@sawo.com",
            Purpose = EmailAccountPurpose.Inbound,
            Protocol = EmailProtocol.Imap,
            Host = "imap.example.com",
            Port = 993,
            Encryption = "SSL/TLS",
            Username = "sales@sawo.com",
            AuthMethod = EmailAuthMethod.Password,
            IsActive = true,
            MonitoringEnabled = true,
            Credential = new EmailCredential
            {
                EncryptedSecret = System.Text.Encoding.UTF8.GetBytes("password"),
                Nonce = Array.Empty<byte>(),
                Tag = Array.Empty<byte>(),
                KeyId = "test",
            },
        };
    }

    private static ProviderMessage Message(string providerId, string subject = "Test", DateTimeOffset? receivedAt = null) => new(
        providerId, $"<{providerId}@example.com>", null, null, Array.Empty<string>(),
        "customer@example.com", "Customer", new[] { "sales@sawo.com" }, Array.Empty<string>(),
        subject, "Body text", null, receivedAt ?? DateTimeOffset.UtcNow, Array.Empty<ProviderAttachmentSummary>());

    /// <summary>Completion gate Q3/Q5 — new messages are persisted with required identifiers preserved.</summary>
    [Fact]
    public async Task RunForAccountAsync_PersistsNewMessagesWithRequiredFields()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        await db.SaveChangesAsync();

        var adapter = new FakeEmailProviderAdapter
        {
            FetchBehavior = (_, _, _, _, _) => Task.FromResult(new FetchInboxResult(
                1, 1, new[] { Message("101") }, Array.Empty<(uint, string)>()))
        };
        var service = new EmailIntakeService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter));

        var result = await service.RunForAccountAsync(account.Id, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.PersistedCount);
        Assert.Equal(0, result.DuplicateCount);

        var stored = await db.EmailMessages.SingleAsync();
        Assert.Equal("101", stored.ProviderMessageId);
        Assert.Equal("customer@example.com", stored.FromAddress);
        Assert.Equal("sales@sawo.com", stored.ToAddresses);
        Assert.Equal("Test", stored.Subject);
        Assert.Equal(EmailProcessingStatus.PendingClassification, stored.ProcessingStatus);
    }

    /// <summary>Completion gate Q4 — the same ProviderMessageId must never create two rows.</summary>
    [Fact]
    public async Task RunForAccountAsync_DoesNotCreateDuplicateOnRepeatedFetchOfSameMessage()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        await db.SaveChangesAsync();

        var adapter = new FakeEmailProviderAdapter
        {
            FetchBehavior = (_, _, afterUid, _, _) => Task.FromResult(new FetchInboxResult(
                1, 1, new[] { Message("101") }, Array.Empty<(uint, string)>()))
        };
        var service = new EmailIntakeService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter));

        // Simulate the adapter returning the same message twice across two separate runs
        // (e.g. a watermark that didn't advance, or a provider re-delivering the same UID).
        await service.RunForAccountAsync(account.Id, CancellationToken.None);
        var second = await service.RunForAccountAsync(account.Id, CancellationToken.None);

        Assert.Equal(1, second.DuplicateCount);
        Assert.Equal(0, second.PersistedCount);
        Assert.Equal(1, await db.EmailMessages.CountAsync());
    }

    /// <summary>Completion gate Q7 — one malformed message must not stop the rest of the batch.</summary>
    [Fact]
    public async Task RunForAccountAsync_PersistsGoodMessagesEvenWhenOthersAreMalformed()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        await db.SaveChangesAsync();

        var adapter = new FakeEmailProviderAdapter
        {
            FetchBehavior = (_, _, _, _, _) => Task.FromResult(new FetchInboxResult(
                1, 3,
                new[] { Message("101"), Message("103") },
                new[] { (102u, "Malformed MIME structure") }))
        };
        var service = new EmailIntakeService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter));

        var result = await service.RunForAccountAsync(account.Id, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.PersistedCount);
        Assert.Equal(1, result.MalformedCount);
        Assert.Equal(2, await db.EmailMessages.CountAsync());

        var malformedLog = await db.EmailIntakeLogs.SingleAsync(l => l.Outcome == EmailIntakeOutcome.Skipped_Malformed);
        Assert.Equal("102", malformedLog.ProviderMessageId);
    }

    /// <summary>Completion gate Q9 — a provider/connection failure must not corrupt the resume watermark.</summary>
    [Fact]
    public async Task RunForAccountAsync_OnProviderFailure_LeavesWatermarkUntouchedAndRecordsFailure()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        var syncState = new EmailSyncState { EmailAccountId = account.Id, LastUidValidity = 1, LastSeenUid = 50 };
        db.EmailSyncStates.Add(syncState);
        await db.SaveChangesAsync();

        var adapter = new FakeEmailProviderAdapter
        {
            FetchBehavior = (_, _, _, _, _) => throw new IOException("Connection reset by peer")
        };
        var service = new EmailIntakeService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter));

        var result = await service.RunForAccountAsync(account.Id, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Connection reset", result.Error);

        var reloaded = await db.EmailSyncStates.SingleAsync(s => s.EmailAccountId == account.Id);
        // §44 — mailbox unavailability must never be treated as "no messages"; the watermark
        // must be exactly what it was before this failed attempt, so the next run retries the
        // same range instead of silently skipping past unfetched messages.
        Assert.Equal(50u, reloaded.LastSeenUid);
        Assert.Equal(1u, reloaded.LastUidValidity);
        Assert.Equal(1, reloaded.ConsecutiveFailureCount);
        Assert.Contains("Connection reset", reloaded.LastSyncError);

        var failureLog = await db.EmailIntakeLogs.SingleAsync(l => l.Outcome == EmailIntakeOutcome.Failed);
        Assert.Contains("Connection reset", failureLog.Detail);
    }

    /// <summary>Completion gate Q11 — the watermark advances so a subsequent run resumes, not re-scans.</summary>
    [Fact]
    public async Task RunForAccountAsync_AdvancesWatermark_SoNextRunOnlyFetchesNewerMessages()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        await db.SaveChangesAsync();

        uint? capturedAfterUid = null;
        var adapter = new FakeEmailProviderAdapter
        {
            FetchBehavior = (_, _, afterUid, _, _) =>
            {
                capturedAfterUid = afterUid;
                return Task.FromResult(new FetchInboxResult(1, 5, new[] { Message("105") }, Array.Empty<(uint, string)>()));
            }
        };
        var service = new EmailIntakeService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter));

        await service.RunForAccountAsync(account.Id, CancellationToken.None);
        await service.RunForAccountAsync(account.Id, CancellationToken.None);

        // Second call must have passed the watermark left by the first call (5), not null/0.
        Assert.Equal(5u, capturedAfterUid);
    }

    /// <summary>Completion gate — deactivated/monitoring-disabled accounts must not be polled.</summary>
    [Fact]
    public async Task RunForAccountAsync_SkipsQuietly_WhenAccountIsNotActivelyMonitored()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        account.MonitoringEnabled = false;
        db.EmailAccounts.Add(account);
        await db.SaveChangesAsync();

        var adapter = new FakeEmailProviderAdapter
        {
            FetchBehavior = (_, _, _, _, _) => throw new InvalidOperationException("Should never be called")
        };
        var service = new EmailIntakeService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter));

        var result = await service.RunForAccountAsync(account.Id, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.FetchedCount);
    }

    /// <summary>Completion gate Q1/Q2 boundary — RunAllAsync only targets active Inbound monitored accounts.</summary>
    [Fact]
    public async Task RunAllAsync_OnlyProcessesActiveMonitoredInboundAccounts()
    {
        using var db = TestDbContext.CreateNew();

        var monitored = CreateAccount();
        var notMonitored = CreateAccount();
        notMonitored.EmailAddress = "other@sawo.com";
        notMonitored.MonitoringEnabled = false;
        var outbound = CreateAccount();
        outbound.EmailAddress = "notify@sawo.com";
        outbound.Purpose = EmailAccountPurpose.Outbound;
        outbound.MonitoringEnabled = false;

        db.EmailAccounts.AddRange(monitored, notMonitored, outbound);
        await db.SaveChangesAsync();

        var processedAccountIds = new List<Guid>();
        var adapter = new FakeEmailProviderAdapter
        {
            FetchBehavior = (settings, _, _, _, _) => Task.FromResult(new FetchInboxResult(1, null, Array.Empty<ProviderMessage>(), Array.Empty<(uint, string)>()))
        };
        var service = new EmailIntakeService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter));

        var results = await service.RunAllAsync(CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(monitored.Id, results[0].EmailAccountId);
    }
}
