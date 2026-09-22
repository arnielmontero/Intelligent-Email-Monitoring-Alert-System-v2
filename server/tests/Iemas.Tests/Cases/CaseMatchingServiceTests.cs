using Iemas.Application.Cases;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Iemas.Tests.TestSupport;
using Xunit;

namespace Iemas.Tests.Cases;

/// <summary>§34 Case Matching priority order, and the §19 hard requirement that subject alone must never determine a match.</summary>
public class CaseMatchingServiceTests
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
        };
    }

    private static EmailMessage CreateMessage(Guid accountId, string subject, string from = "customer@example.com",
        string? threadId = null, string? messageId = null, string? inReplyTo = null, string? references = null,
        DateTimeOffset? receivedAt = null)
    {
        return new EmailMessage
        {
            EmailAccountId = accountId,
            Provider = EmailProtocol.Imap,
            ProviderMessageId = Guid.NewGuid().ToString(),
            MessageId = messageId,
            ThreadId = threadId,
            InReplyTo = inReplyTo,
            References = references,
            FromAddress = from,
            ToAddresses = "sales@sawo.com",
            Subject = subject,
            ReceivedAt = receivedAt ?? DateTimeOffset.UtcNow,
            ProcessingStatus = EmailProcessingStatus.Processed,
        };
    }

    [Theory]
    [InlineData("Re: Product Inquiry", "Product Inquiry")]
    [InlineData("RE: Product Inquiry", "Product Inquiry")]
    [InlineData("Fwd: Product Inquiry", "Product Inquiry")]
    [InlineData("FW: Product Inquiry", "Product Inquiry")]
    [InlineData("Re: Re: Product Inquiry", "Product Inquiry")]
    [InlineData("Product Inquiry", "Product Inquiry")]
    public void NormalizeSubject_StripsReplyForwardPrefixes(string input, string expected)
    {
        Assert.Equal(expected, CaseMatchingService.NormalizeSubject(input));
    }

    [Fact]
    public async Task FindMatchingCaseAsync_NoExistingCases_ReturnsNewCaseSignal()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        await db.SaveChangesAsync();

        var message = CreateMessage(account.Id, "Price Request");
        var service = new CaseMatchingService(db);

        var result = await service.FindMatchingCaseAsync(message, CancellationToken.None);

        Assert.Null(result.ExistingCase);
        Assert.Equal(CaseMatchSignal.NewCase, result.Signal);
    }

    [Fact]
    public async Task FindMatchingCaseAsync_MatchesByThreadId_BeforeAnyWeakerSignal()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);

        var existingCase = new Case { CaseNumber = "CASE-000001", EmailAccountId = account.Id, CustomerEmailAddress = "customer@example.com", Subject = "X", NormalizedSubject = "X", LastActivityAt = DateTimeOffset.UtcNow.AddDays(-10) };
        var priorMessage = CreateMessage(account.Id, "Different subject entirely", threadId: "thread-123");
        db.Cases.Add(existingCase);
        db.EmailMessages.Add(priorMessage);
        await db.SaveChangesAsync();
        db.CaseEmails.Add(new CaseEmail { CaseId = existingCase.Id, EmailMessageId = priorMessage.Id, MatchSignal = CaseMatchSignal.NewCase });
        await db.SaveChangesAsync();

        // Same thread ID but a totally different subject and (hypothetically) a different
        // customer — thread ID must win over everything else, including subject mismatch.
        var newMessage = CreateMessage(account.Id, "Totally unrelated subject", from: "other@example.com", threadId: "thread-123");
        var service = new CaseMatchingService(db);

        var result = await service.FindMatchingCaseAsync(newMessage, CancellationToken.None);

        Assert.Equal(existingCase.Id, result.ExistingCase?.Id);
        Assert.Equal(CaseMatchSignal.ThreadId, result.Signal);
    }

    [Fact]
    public async Task FindMatchingCaseAsync_MatchesByInReplyTo()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        var existingCase = new Case { CaseNumber = "CASE-000001", EmailAccountId = account.Id, CustomerEmailAddress = "customer@example.com", Subject = "X", NormalizedSubject = "X", LastActivityAt = DateTimeOffset.UtcNow };
        var priorMessage = CreateMessage(account.Id, "Original", messageId: "<original@example.com>");
        db.Cases.Add(existingCase);
        db.EmailMessages.Add(priorMessage);
        await db.SaveChangesAsync();
        db.CaseEmails.Add(new CaseEmail { CaseId = existingCase.Id, EmailMessageId = priorMessage.Id, MatchSignal = CaseMatchSignal.NewCase });
        await db.SaveChangesAsync();

        var reply = CreateMessage(account.Id, "Re: Original", inReplyTo: "<original@example.com>");
        var service = new CaseMatchingService(db);

        var result = await service.FindMatchingCaseAsync(reply, CancellationToken.None);

        Assert.Equal(existingCase.Id, result.ExistingCase?.Id);
        Assert.Equal(CaseMatchSignal.InReplyTo, result.Signal);
    }

    [Fact]
    public async Task FindMatchingCaseAsync_MatchesByReferences()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        var existingCase = new Case { CaseNumber = "CASE-000001", EmailAccountId = account.Id, CustomerEmailAddress = "customer@example.com", Subject = "X", NormalizedSubject = "X", LastActivityAt = DateTimeOffset.UtcNow };
        var priorMessage = CreateMessage(account.Id, "Original", messageId: "<original@example.com>");
        db.Cases.Add(existingCase);
        db.EmailMessages.Add(priorMessage);
        await db.SaveChangesAsync();
        db.CaseEmails.Add(new CaseEmail { CaseId = existingCase.Id, EmailMessageId = priorMessage.Id, MatchSignal = CaseMatchSignal.NewCase });
        await db.SaveChangesAsync();

        var laterReply = CreateMessage(account.Id, "Re: Original", references: "<other@example.com> <original@example.com>");
        var service = new CaseMatchingService(db);

        var result = await service.FindMatchingCaseAsync(laterReply, CancellationToken.None);

        Assert.Equal(existingCase.Id, result.ExistingCase?.Id);
        Assert.Equal(CaseMatchSignal.References, result.Signal);
    }

    [Fact]
    public async Task FindMatchingCaseAsync_MatchesOpenCaseBySameParticipant_WhenNoThreadSignalsPresent()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        var existingCase = new Case { CaseNumber = "CASE-000001", EmailAccountId = account.Id, CustomerEmailAddress = "customer@example.com", Subject = "First", NormalizedSubject = "first", WorkStatus = CaseWorkStatus.ActionRequired, LastActivityAt = DateTimeOffset.UtcNow };
        db.Cases.Add(existingCase);
        await db.SaveChangesAsync();

        // No thread/reply headers at all — a genuinely new email from the same customer on the
        // same account, while their existing Case is still open.
        var newMessage = CreateMessage(account.Id, "A completely different subject line");
        var service = new CaseMatchingService(db);

        var result = await service.FindMatchingCaseAsync(newMessage, CancellationToken.None);

        Assert.Equal(existingCase.Id, result.ExistingCase?.Id);
        Assert.Equal(CaseMatchSignal.ParticipantAndAccountRelationship, result.Signal);
    }

    /// <summary>§37 — same customer does not automatically mean same Case: a Completed case must not silently reabsorb an unrelated new message via the open-participant signal.</summary>
    [Fact]
    public async Task FindMatchingCaseAsync_DoesNotMatchCompletedCase_ViaParticipantSignal()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        var completedCase = new Case
        {
            CaseNumber = "CASE-000001", EmailAccountId = account.Id, CustomerEmailAddress = "customer@example.com",
            Subject = "Old topic", NormalizedSubject = "old topic", WorkStatus = CaseWorkStatus.Completed,
            LastActivityAt = DateTimeOffset.UtcNow.AddDays(-30),
        };
        db.Cases.Add(completedCase);
        await db.SaveChangesAsync();

        var newMessage = CreateMessage(account.Id, "Website Access Problem", receivedAt: DateTimeOffset.UtcNow);
        var service = new CaseMatchingService(db);

        var result = await service.FindMatchingCaseAsync(newMessage, CancellationToken.None);

        // Falls through to NewCase, not the stale completed case — recency window (48h) has also
        // long since passed, so signal 6 doesn't fire either.
        Assert.Equal(CaseMatchSignal.NewCase, result.Signal);
    }

    [Fact]
    public async Task FindMatchingCaseAsync_NeverMatchesDifferentCustomer_OnSubjectAlone()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        var existingCase = new Case
        {
            CaseNumber = "CASE-000001", EmailAccountId = account.Id, CustomerEmailAddress = "customer-a@example.com",
            Subject = "Price Request", NormalizedSubject = "price request", WorkStatus = CaseWorkStatus.ActionRequired,
            LastActivityAt = DateTimeOffset.UtcNow,
        };
        db.Cases.Add(existingCase);
        await db.SaveChangesAsync();

        // Different customer, same normalized subject, no thread/reply signals at all.
        var newMessage = CreateMessage(account.Id, "Price Request", from: "customer-b@example.com");
        var service = new CaseMatchingService(db);

        var result = await service.FindMatchingCaseAsync(newMessage, CancellationToken.None);

        // §34 hard requirement — must never match on subject alone across different customers.
        Assert.Equal(CaseMatchSignal.NewCase, result.Signal);
        Assert.Null(result.ExistingCase);
    }

    [Fact]
    public async Task FindMatchingCaseAsync_MatchesBySubject_OnlyAsLastResort_ForSameCustomer()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        // Completed (so the open-participant signal #5 doesn't fire) and outside the recent-
        // conversation window (so #6 doesn't fire either) — isolates the subject-match path (#7).
        var existingCase = new Case
        {
            CaseNumber = "CASE-000001", EmailAccountId = account.Id, CustomerEmailAddress = "customer@example.com",
            Subject = "Price Request", NormalizedSubject = "price request", WorkStatus = CaseWorkStatus.Completed,
            LastActivityAt = DateTimeOffset.UtcNow.AddDays(-10),
        };
        db.Cases.Add(existingCase);
        await db.SaveChangesAsync();

        var newMessage = CreateMessage(account.Id, "Re: Price Request", receivedAt: DateTimeOffset.UtcNow);
        var service = new CaseMatchingService(db);

        var result = await service.FindMatchingCaseAsync(newMessage, CancellationToken.None);

        Assert.Equal(existingCase.Id, result.ExistingCase?.Id);
        Assert.Equal(CaseMatchSignal.NormalizedSubjectWeakSignal, result.Signal);
    }
}
