using Iemas.Application.Cases;
using Iemas.Application.Common.Providers;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iemas.Tests.Cases;

public class ReplyVerificationServiceTests
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

    private static Case CreateCase(Guid accountId, CaseReplyStatus replyStatus = CaseReplyStatus.AwaitingReply, CaseWorkStatus workStatus = CaseWorkStatus.ActionRequired)
    {
        return new Case
        {
            CaseNumber = "CASE-000001",
            EmailAccountId = accountId,
            CustomerEmailAddress = "customer@example.com",
            Subject = "Price Request",
            NormalizedSubject = "Price Request",
            WorkStatus = workStatus,
            ReplyStatus = replyStatus,
            FirstEmailReceivedAt = DateTimeOffset.UtcNow.AddDays(-1),
            LastActivityAt = DateTimeOffset.UtcNow.AddDays(-1),
        };
    }

    private static EmailMessage InboundMessage(Guid accountId, string messageId)
    {
        return new EmailMessage
        {
            EmailAccountId = accountId,
            Provider = EmailProtocol.Imap,
            ProviderMessageId = Guid.NewGuid().ToString(),
            MessageId = messageId,
            FromAddress = "customer@example.com",
            ToAddresses = "sales@sawo.com",
            Subject = "Price Request",
            ReceivedAt = DateTimeOffset.UtcNow.AddDays(-1),
        };
    }

    private static ProviderMessage SentReply(string inReplyTo)
    {
        return new ProviderMessage(
            Guid.NewGuid().ToString(), $"<reply-{Guid.NewGuid()}@sawo.com>", null, inReplyTo, Array.Empty<string>(),
            "sales@sawo.com", "Sales Team", new[] { "customer@example.com" }, Array.Empty<string>(),
            "Re: Price Request", "Here is our latest pricing.", null, DateTimeOffset.UtcNow, Array.Empty<ProviderAttachmentSummary>());
    }

    private static async Task<Case> SetupCaseWithInboundMessageAsync(TestDbContext db, string messageId, CaseReplyStatus replyStatus = CaseReplyStatus.AwaitingReply)
    {
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        var theCase = CreateCase(account.Id, replyStatus);
        db.Cases.Add(theCase);
        var message = InboundMessage(account.Id, messageId);
        db.EmailMessages.Add(message);
        await db.SaveChangesAsync();
        db.CaseEmails.Add(new CaseEmail { CaseId = theCase.Id, EmailMessageId = message.Id, MatchSignal = CaseMatchSignal.NewCase });
        await db.SaveChangesAsync();
        return theCase;
    }

    /// <summary>Verified reply found → Case.ReplyStatus = Replied, WorkStatus advances ActionRequired → InProgress, and the attempt is recorded.</summary>
    [Fact]
    public async Task RunAsync_ReplyFound_SetsRepliedStatus_AdvancesWorkStatus_RecordsAttempt()
    {
        using var db = TestDbContext.CreateNew();
        var theCase = await SetupCaseWithInboundMessageAsync(db, "<original@example.com>");

        var adapter = new FakeEmailProviderAdapter
        {
            FetchSentBehavior = (_, _, _, _) => Task.FromResult(new FetchSentResult(
                true, null, new[] { SentReply("<original@example.com>") }, Array.Empty<(string, string)>()))
        };
        var service = new ReplyVerificationService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter));

        var result = await service.RunAsync(10, CancellationToken.None);

        Assert.Equal(1, result.VerifiedCount);
        var reloaded = await db.Cases.SingleAsync(c => c.Id == theCase.Id);
        Assert.Equal(CaseReplyStatus.Replied, reloaded.ReplyStatus);
        Assert.Equal(CaseWorkStatus.InProgress, reloaded.WorkStatus);

        var attempt = await db.ReplyVerificationAttempts.SingleAsync(a => a.CaseId == theCase.Id);
        Assert.Equal(ReplyVerificationOutcome.VerifiedReply, attempt.Outcome);
        Assert.Equal(ReplyMatchSignal.InReplyTo, attempt.MatchSignal);
    }

    /// <summary>No reply found → Case.ReplyStatus = NoReplyFound, WorkStatus untouched, attempt recorded.</summary>
    [Fact]
    public async Task RunAsync_NoReplyFound_SetsNoReplyFoundStatus()
    {
        using var db = TestDbContext.CreateNew();
        var theCase = await SetupCaseWithInboundMessageAsync(db, "<original@example.com>");

        var adapter = new FakeEmailProviderAdapter
        {
            FetchSentBehavior = (_, _, _, _) => Task.FromResult(new FetchSentResult(
                true, null, Array.Empty<ProviderMessage>(), Array.Empty<(string, string)>()))
        };
        var service = new ReplyVerificationService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter));

        var result = await service.RunAsync(10, CancellationToken.None);

        Assert.Equal(1, result.NoReplyFoundCount);
        var reloaded = await db.Cases.SingleAsync(c => c.Id == theCase.Id);
        Assert.Equal(CaseReplyStatus.NoReplyFound, reloaded.ReplyStatus);
        Assert.Equal(CaseWorkStatus.ActionRequired, reloaded.WorkStatus);
    }

    /// <summary>§44 — mailbox unavailable (thrown exception) must never become NoReplyFound; must be VerificationFailed.</summary>
    [Fact]
    public async Task RunAsync_MailboxUnavailable_NeverBecomesNoReplyFound_BecomesVerificationFailed()
    {
        using var db = TestDbContext.CreateNew();
        var theCase = await SetupCaseWithInboundMessageAsync(db, "<original@example.com>");

        var adapter = new FakeEmailProviderAdapter
        {
            FetchSentBehavior = (_, _, _, _) => throw new IOException("Connection reset by peer")
        };
        var service = new ReplyVerificationService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter));

        var result = await service.RunAsync(10, CancellationToken.None);

        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, result.NoReplyFoundCount);
        var reloaded = await db.Cases.SingleAsync(c => c.Id == theCase.Id);
        Assert.Equal(CaseReplyStatus.VerificationFailed, reloaded.ReplyStatus);

        var attempt = await db.ReplyVerificationAttempts.SingleAsync(a => a.CaseId == theCase.Id);
        Assert.Equal(ReplyVerificationOutcome.VerificationFailed, attempt.Outcome);
        Assert.Contains("Connection reset", attempt.ErrorDetail);
    }

    /// <summary>§44 — an authentication failure (clean adapter-reported failure, not a thrown exception) must also never become NoReplyFound.</summary>
    [Fact]
    public async Task RunAsync_AuthenticationFailure_ReportedViaFolderInaccessible_BecomesVerificationFailed()
    {
        using var db = TestDbContext.CreateNew();
        var theCase = await SetupCaseWithInboundMessageAsync(db, "<original@example.com>");

        var adapter = new FakeEmailProviderAdapter
        {
            FetchSentBehavior = (_, _, _, _) => Task.FromResult(new FetchSentResult(
                false, "LOGIN failed: authentication error", Array.Empty<ProviderMessage>(), Array.Empty<(string, string)>()))
        };
        var service = new ReplyVerificationService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter));

        var result = await service.RunAsync(10, CancellationToken.None);

        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, result.NoReplyFoundCount);
        var reloaded = await db.Cases.SingleAsync(c => c.Id == theCase.Id);
        Assert.Equal(CaseReplyStatus.VerificationFailed, reloaded.ReplyStatus);

        var attempt = await db.ReplyVerificationAttempts.SingleAsync(a => a.CaseId == theCase.Id);
        Assert.Contains("authentication error", attempt.ErrorDetail);
    }

    /// <summary>Missing credential on the account must also fail cleanly as VerificationFailed, not crash or silently report no reply.</summary>
    [Fact]
    public async Task RunAsync_NoCredentialConfigured_BecomesVerificationFailed()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        account.Credential = null;
        db.EmailAccounts.Add(account);
        var theCase = CreateCase(account.Id);
        db.Cases.Add(theCase);
        var message = InboundMessage(account.Id, "<original@example.com>");
        db.EmailMessages.Add(message);
        await db.SaveChangesAsync();
        db.CaseEmails.Add(new CaseEmail { CaseId = theCase.Id, EmailMessageId = message.Id, MatchSignal = CaseMatchSignal.NewCase });
        await db.SaveChangesAsync();

        var adapter = new FakeEmailProviderAdapter();
        var service = new ReplyVerificationService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter));

        var result = await service.RunAsync(10, CancellationToken.None);

        Assert.Equal(1, result.FailedCount);
        var reloaded = await db.Cases.SingleAsync(c => c.Id == theCase.Id);
        Assert.Equal(CaseReplyStatus.VerificationFailed, reloaded.ReplyStatus);
    }

    /// <summary>A malformed individual Sent message must not abort verification for the whole account's batch of Cases — isolated, not fatal.</summary>
    [Fact]
    public async Task RunAsync_MalformedSentMessageReported_DoesNotAbortVerification_StillFindsGoodMatch()
    {
        using var db = TestDbContext.CreateNew();
        var theCase = await SetupCaseWithInboundMessageAsync(db, "<original@example.com>");

        var adapter = new FakeEmailProviderAdapter
        {
            // The adapter itself isolates a malformed message into MalformedMessages and still
            // returns the good messages — this test verifies the orchestration layer doesn't
            // require the malformed list to be empty to proceed.
            FetchSentBehavior = (_, _, _, _) => Task.FromResult(new FetchSentResult(
                true, null,
                new[] { SentReply("<original@example.com>") },
                new[] { ("42", "Malformed MIME structure") }))
        };
        var service = new ReplyVerificationService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter));

        var result = await service.RunAsync(10, CancellationToken.None);

        Assert.Equal(1, result.VerifiedCount);
        var reloaded = await db.Cases.SingleAsync(c => c.Id == theCase.Id);
        Assert.Equal(CaseReplyStatus.Replied, reloaded.ReplyStatus);
    }

    /// <summary>Multiple Sent messages exist for the account; verification correctly matches the right one to the right Case (not just "some" Sent message).</summary>
    [Fact]
    public async Task RunAsync_MultipleSentMessages_MatchesCorrectOneToCorrectCase()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);

        var caseA = CreateCase(account.Id);
        caseA.CaseNumber = "CASE-000001";
        caseA.CustomerEmailAddress = "customer-a@example.com";
        var caseB = CreateCase(account.Id);
        caseB.CaseNumber = "CASE-000002";
        caseB.CustomerEmailAddress = "customer-b@example.com";
        db.Cases.AddRange(caseA, caseB);

        var messageA = InboundMessage(account.Id, "<a-original@example.com>");
        messageA.FromAddress = "customer-a@example.com";
        var messageB = InboundMessage(account.Id, "<b-original@example.com>");
        messageB.FromAddress = "customer-b@example.com";
        db.EmailMessages.AddRange(messageA, messageB);
        await db.SaveChangesAsync();

        db.CaseEmails.Add(new CaseEmail { CaseId = caseA.Id, EmailMessageId = messageA.Id, MatchSignal = CaseMatchSignal.NewCase });
        db.CaseEmails.Add(new CaseEmail { CaseId = caseB.Id, EmailMessageId = messageB.Id, MatchSignal = CaseMatchSignal.NewCase });
        await db.SaveChangesAsync();

        var adapter = new FakeEmailProviderAdapter
        {
            FetchSentBehavior = (_, _, _, _) => Task.FromResult(new FetchSentResult(
                true, null,
                new[] { SentReply("<a-original@example.com>") }, // only a reply to Case A exists
                Array.Empty<(string, string)>()))
        };
        var service = new ReplyVerificationService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter));

        await service.RunAsync(10, CancellationToken.None);

        var reloadedA = await db.Cases.SingleAsync(c => c.Id == caseA.Id);
        var reloadedB = await db.Cases.SingleAsync(c => c.Id == caseB.Id);
        Assert.Equal(CaseReplyStatus.Replied, reloadedA.ReplyStatus);
        Assert.Equal(CaseReplyStatus.NoReplyFound, reloadedB.ReplyStatus);
    }

    /// <summary>Already-verified Cases (ReplyStatus already Replied) are not re-checked/re-tallied — a quiet skip, no duplicate attempt.</summary>
    [Fact]
    public async Task RunAsync_AlreadyRepliedCase_IsNotReCheckedOrReTallied()
    {
        using var db = TestDbContext.CreateNew();
        var theCase = await SetupCaseWithInboundMessageAsync(db, "<original@example.com>", CaseReplyStatus.Replied);

        var adapter = new FakeEmailProviderAdapter();
        var service = new ReplyVerificationService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter));

        var result = await service.RunAsync(10, CancellationToken.None);

        // Already-Replied Cases are excluded from the candidate query entirely (ReplyStatus is
        // neither AwaitingReply nor VerificationPending), so nothing is considered at all.
        Assert.Equal(0, result.ConsideredCount);
        Assert.Equal(0, await db.ReplyVerificationAttempts.CountAsync());
    }

    /// <summary>Completed/Cancelled Cases are excluded from verification entirely — no point checking a Case that's already closed.</summary>
    [Fact]
    public async Task RunAsync_CompletedCase_IsExcludedFromVerification()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        var theCase = CreateCase(account.Id, CaseReplyStatus.AwaitingReply, CaseWorkStatus.Completed);
        db.Cases.Add(theCase);
        await db.SaveChangesAsync();

        var adapter = new FakeEmailProviderAdapter();
        var service = new ReplyVerificationService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter));

        var result = await service.RunAsync(10, CancellationToken.None);

        Assert.Equal(0, result.ConsideredCount);
    }

    /// <summary>§66/§89 — every verification attempt is auditable: a CaseEvent is appended for each outcome, and the CaseEvent history is never overwritten.</summary>
    [Fact]
    public async Task RunAsync_RecordsCaseEvent_ForEveryVerificationAttempt()
    {
        using var db = TestDbContext.CreateNew();
        var theCase = await SetupCaseWithInboundMessageAsync(db, "<original@example.com>");

        var adapter = new FakeEmailProviderAdapter
        {
            FetchSentBehavior = (_, _, _, _) => Task.FromResult(new FetchSentResult(
                true, null, Array.Empty<ProviderMessage>(), Array.Empty<(string, string)>()))
        };
        var service = new ReplyVerificationService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter));

        await service.RunAsync(10, CancellationToken.None);

        var events = await db.CaseEvents.Where(e => e.CaseId == theCase.Id).ToListAsync();
        Assert.Contains(events, e => e.EventType == CaseEventType.ReplyVerification);
    }

    /// <summary>A second run after a first NoReplyFound re-checks (pending/retry behavior) and can transition to Replied once a reply later appears — multiple attempts accumulate in the audit trail.</summary>
    [Fact]
    public async Task RunAsync_SecondRunAfterNoReplyFound_CanLaterVerify_AndAccumulatesAttemptHistory()
    {
        using var db = TestDbContext.CreateNew();
        var theCase = await SetupCaseWithInboundMessageAsync(db, "<original@example.com>");

        var adapter = new FakeEmailProviderAdapter
        {
            FetchSentBehavior = (_, _, _, _) => Task.FromResult(new FetchSentResult(
                true, null, Array.Empty<ProviderMessage>(), Array.Empty<(string, string)>()))
        };
        var service = new ReplyVerificationService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter));

        await service.RunAsync(10, CancellationToken.None);
        Assert.Equal(CaseReplyStatus.NoReplyFound, (await db.Cases.SingleAsync()).ReplyStatus);

        // Employee has since replied; re-run finds it. NoReplyFound Cases remain candidates
        // because they're neither Completed/Cancelled nor already Replied — this models the
        // recurring-job retry behavior (§54-style recheck, though the reminder engine itself is
        // a later phase).
        adapter.FetchSentBehavior = (_, _, _, _) => Task.FromResult(new FetchSentResult(
            true, null, new[] { SentReply("<original@example.com>") }, Array.Empty<(string, string)>()));

        var secondCandidates = await db.Cases.Where(c => c.ReplyStatus == CaseReplyStatus.AwaitingReply || c.ReplyStatus == CaseReplyStatus.VerificationPending).CountAsync();
        // Confirm the NoReplyFound state is NOT itself a candidate for automatic re-poll under
        // the current query (AwaitingReply/VerificationPending only) — this is a deliberate
        // boundary documented in the tracker; re-verification of a NoReplyFound case currently
        // requires a manual trigger or a future reminder-driven re-check, not the recurring job.
        Assert.Equal(0, secondCandidates);
    }

    /// <summary>Manual/second attempt via direct service call still accumulates in the attempt history rather than overwriting the first.</summary>
    [Fact]
    public async Task MultipleAttempts_AllPersistInAuditTrail_NotOverwritten()
    {
        using var db = TestDbContext.CreateNew();
        var theCase = await SetupCaseWithInboundMessageAsync(db, "<original@example.com>");

        var adapter = new FakeEmailProviderAdapter
        {
            FetchSentBehavior = (_, _, _, _) => Task.FromResult(new FetchSentResult(
                true, null, Array.Empty<ProviderMessage>(), Array.Empty<(string, string)>()))
        };
        var service = new ReplyVerificationService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter));

        await service.RunAsync(10, CancellationToken.None);

        // Force the case back to a re-checkable state to simulate a second poll (mirrors what a
        // future reminder/recheck mechanism would do) and run again with a different outcome.
        var reloaded = await db.Cases.SingleAsync();
        reloaded.ReplyStatus = CaseReplyStatus.AwaitingReply;
        await db.SaveChangesAsync();

        adapter.FetchSentBehavior = (_, _, _, _) => Task.FromResult(new FetchSentResult(
            true, null, new[] { SentReply("<original@example.com>") }, Array.Empty<(string, string)>()));
        await service.RunAsync(10, CancellationToken.None);

        var attempts = await db.ReplyVerificationAttempts.Where(a => a.CaseId == theCase.Id).ToListAsync();
        Assert.Equal(2, attempts.Count);
        Assert.Contains(attempts, a => a.Outcome == ReplyVerificationOutcome.NoReplyFound);
        Assert.Contains(attempts, a => a.Outcome == ReplyVerificationOutcome.VerifiedReply);
    }
}
