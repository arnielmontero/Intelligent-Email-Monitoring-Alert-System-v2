using Iemas.Application.Cases.Dtos;
using Iemas.Application.Cases;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Iemas.Domain.Ai;
using Microsoft.EntityFrameworkCore;
using Iemas.Domain.Identity;
using Iemas.Tests.TestSupport;
using Xunit;

namespace Iemas.Tests.Cases;

public class CaseServiceTests
{
    private static Case CreateCase(string caseNumber, string mailbox = "sales@sawo.com", string customer = "customer@example.com", Employee? owner = null) => new()
    {
        CaseNumber = caseNumber,
        EmailAccount = new EmailAccount { EmailAddress = mailbox, Host = "imap.example.com", Username = mailbox, Encryption = "SSL/TLS" },
        OwnerEmployee = owner,
        CustomerEmailAddress = customer,
        Subject = "Test subject",
        NormalizedSubject = "Test subject",
        WorkStatus = CaseWorkStatus.ActionRequired,
        ReplyStatus = CaseReplyStatus.AwaitingReply,
        FirstEmailReceivedAt = DateTimeOffset.UtcNow,
        LastActivityAt = DateTimeOffset.UtcNow,
    };

    /// <summary>§66/§89 — global, cross-Case search, newest first, correctly carrying Case-identifying info alongside each event.</summary>
    [Fact]
    public async Task SearchEventsAsync_ReturnsNewestFirst_WithCaseInfo()
    {
        using var db = TestDbContext.CreateNew();
        var case1 = CreateCase("CASE-000001");
        var case2 = CreateCase("CASE-000002");
        db.Cases.AddRange(case1, case2);
        await db.SaveChangesAsync();

        db.CaseEvents.Add(new CaseEvent { CaseId = case1.Id, EventType = CaseEventType.Created, Detail = "First", OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-2) });
        db.CaseEvents.Add(new CaseEvent { CaseId = case2.Id, EventType = CaseEventType.EmployeeComment, Detail = "Second", OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        await db.SaveChangesAsync();

        var service = new CaseService(db);
        var results = (await service.SearchEventsAsync(new CaseEventSearchFilter(), 1, 100, CancellationToken.None)).Items;

        Assert.Equal(2, results.Count);
        Assert.Equal("Second", results[0].Detail);
        Assert.Equal("CASE-000002", results[0].CaseNumber);
        Assert.Equal("First", results[1].Detail);
    }

    [Fact]
    public async Task SearchEventsAsync_FilterByCaseId_ReturnsOnlyThatCase()
    {
        using var db = TestDbContext.CreateNew();
        var case1 = CreateCase("CASE-000001");
        var case2 = CreateCase("CASE-000002");
        db.Cases.AddRange(case1, case2);
        await db.SaveChangesAsync();

        db.CaseEvents.Add(new CaseEvent { CaseId = case1.Id, EventType = CaseEventType.Created, Detail = "A" });
        db.CaseEvents.Add(new CaseEvent { CaseId = case2.Id, EventType = CaseEventType.Created, Detail = "B" });
        await db.SaveChangesAsync();

        var service = new CaseService(db);
        var results = (await service.SearchEventsAsync(new CaseEventSearchFilter(CaseId: case1.Id), 1, 100, CancellationToken.None)).Items;

        var only = Assert.Single(results);
        Assert.Equal("A", only.Detail);
    }

    [Fact]
    public async Task SearchEventsAsync_FilterByEventType_ReturnsOnlyMatching()
    {
        using var db = TestDbContext.CreateNew();
        var theCase = CreateCase("CASE-000001");
        db.Cases.Add(theCase);
        await db.SaveChangesAsync();

        db.CaseEvents.Add(new CaseEvent { CaseId = theCase.Id, EventType = CaseEventType.Created, Detail = "Create" });
        db.CaseEvents.Add(new CaseEvent { CaseId = theCase.Id, EventType = CaseEventType.EmployeeComment, Detail = "Comment" });
        await db.SaveChangesAsync();

        var service = new CaseService(db);
        var results = (await service.SearchEventsAsync(new CaseEventSearchFilter(EventType: CaseEventType.EmployeeComment), 1, 100, CancellationToken.None)).Items;

        var only = Assert.Single(results);
        Assert.Equal("Comment", only.Detail);
    }

    [Fact]
    public async Task SearchEventsAsync_ActorEmployeeName_ResolvedFromEmployeeId()
    {
        using var db = TestDbContext.CreateNew();
        var employee = new Employee { FullName = "Jane Doe", Email = "jane@sawo.com" };
        db.Employees.Add(employee);
        var theCase = CreateCase("CASE-000001");
        db.Cases.Add(theCase);
        await db.SaveChangesAsync();

        db.CaseEvents.Add(new CaseEvent { CaseId = theCase.Id, EventType = CaseEventType.EmployeeAction, Detail = "Acknowledged", ActorEmployeeId = employee.Id });
        await db.SaveChangesAsync();

        var service = new CaseService(db);
        var results = (await service.SearchEventsAsync(new CaseEventSearchFilter(), 1, 100, CancellationToken.None)).Items;

        var only = Assert.Single(results);
        Assert.Equal("Jane Doe", only.ActorEmployeeName);
    }

    /// <summary>One search box finds events by customer, mailbox or owner; results are paged.</summary>
    [Fact]
    public async Task SearchEventsAsync_SearchMatchesCustomerMailboxAndOwner_AndPages()
    {
        using var db = TestDbContext.CreateNew();
        var owner = new Employee { FullName = "Arniel Montero", Email = "arniel.montero@sawo.com" };
        var mine = CreateCase("CASE-000100", mailbox: "arniel.montero@sawo.com", customer: "maria@customer.example", owner: owner);
        var other = CreateCase("CASE-000101", mailbox: "sales@sawo.com", customer: "bob@elsewhere.example");
        db.Cases.AddRange(mine, other);
        for (var i = 0; i < 30; i++)
        {
            db.CaseEvents.Add(new CaseEvent { CaseId = mine.Id, EventType = CaseEventType.ReminderEvent, Detail = $"Reminder #{i} sent.", OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-i) });
        }
        db.CaseEvents.Add(new CaseEvent { CaseId = other.Id, EventType = CaseEventType.Created, Detail = "Created." });
        await db.SaveChangesAsync();
        var service = new CaseService(db);

        foreach (var term in new[] { "MARIA@customer", "arniel.montero@sawo", "Arniel", "case-000100" })
        {
            var result = await service.SearchEventsAsync(new CaseEventSearchFilter(Search: term), 1, 10, CancellationToken.None);
            Assert.Equal(30, result.TotalCount);
            Assert.Equal(3, result.TotalPages);
            Assert.Equal(10, result.Items.Count);
            Assert.All(result.Items, e => Assert.Equal("arniel.montero@sawo.com", e.MailboxAddress));
            Assert.Equal("Arniel Montero", result.Items[0].OwnerEmployeeName);
            Assert.Equal("maria@customer.example", result.Items[0].CustomerEmailAddress);
        }

        var byMailbox = await service.SearchEventsAsync(new CaseEventSearchFilter(EmailAccountId: other.EmailAccountId), 1, 10, CancellationToken.None);
        Assert.Equal("CASE-000101", Assert.Single(byMailbox.Items).CaseNumber);
    }

    /// <summary>The Case page shows what the email actually said and why the AI made it a Case; HTML-only mail is shown as text.</summary>
    [Fact]
    public async Task GetDetailAsync_IncludesEmailContentAndClassification()
    {
        using var db = TestDbContext.CreateNew();
        var c = CreateCase("CASE-000200");
        db.Cases.Add(c);
        var message = new EmailMessage
        {
            EmailAccountId = c.EmailAccountId, ProviderMessageId = "1", FromAddress = "maria@customer.example", FromDisplayName = "Maria",
            ToAddresses = "sales@sawo.com", Subject = "Quote please", BodyText = null,
            BodyHtml = "<html><style>p{color:red}</style><p>Hello,<br>please quote 20 units &amp; delivery.</p><script>alert(1)</script></html>",
            ReceivedAt = DateTimeOffset.UtcNow,
        };
        db.EmailMessages.Add(message);
        db.CaseEmails.Add(new CaseEmail { CaseId = c.Id, EmailMessageId = message.Id, MatchSignal = CaseMatchSignal.NewCase });
        db.EmailClassifications.Add(new Iemas.Domain.Ai.EmailClassification
        {
            EmailMessageId = message.Id, Decision = ImportanceDecision.Important, Category = "PRICE_REQUEST",
            Priority = ClassificationPriority.High, AiConfidence = 0.92, Summary = "Customer wants a quote for 20 units.", AiModel = "openai/gpt-4o-mini",
        });
        await db.SaveChangesAsync();

        var detail = await new CaseService(db).GetDetailAsync(c.Id, CancellationToken.None);

        var email = Assert.Single(detail!.Emails);
        Assert.True(email.BodyFromHtml);
        Assert.Equal("Hello,\nplease quote 20 units & delivery.", email.Body);
        Assert.DoesNotContain("alert", email.Body);
        Assert.Equal("Maria", email.FromDisplayName);
        Assert.Equal("Important", email.Classification!.Decision);
        Assert.Equal("Customer wants a quote for 20 units.", email.Classification.Summary);
    }

    /// <summary>Each email shows what AI spent on it (retries and failures included); an email the AI never saw shows zero calls; the inquiry shows the sum plus its resolution checks.</summary>
    [Fact]
    public async Task GetDetailAsync_ShowsAiCostPerEmail_AndForTheWholeInquiry()
    {
        using var db = TestDbContext.CreateNew();
        var c = CreateCase("CASE-000201");
        db.Cases.Add(c);
        EmailMessage Message(string id, string subject) => new()
        {
            EmailAccountId = c.EmailAccountId, ProviderMessageId = id, FromAddress = "maria@customer.example", ToAddresses = "sales@sawo.com",
            Subject = subject, BodyText = "text", ReceivedAt = DateTimeOffset.UtcNow,
        };
        var needsAi = Message("1", "Unclear request");
        var filterOnly = Message("2", "Quote please");
        var ourReply = Message("3", "RE: Quote please");
        db.EmailMessages.AddRange(needsAi, filterOnly, ourReply);
        db.CaseEmails.AddRange(
            new CaseEmail { CaseId = c.Id, EmailMessageId = needsAi.Id, MatchSignal = CaseMatchSignal.NewCase },
            new CaseEmail { CaseId = c.Id, EmailMessageId = filterOnly.Id, MatchSignal = CaseMatchSignal.References },
            new CaseEmail { CaseId = c.Id, EmailMessageId = ourReply.Id, MatchSignal = CaseMatchSignal.References, Role = ConversationRole.Agent });
        db.AiUsageRecords.AddRange(
            // the first attempt failed and was retried: both cost tokens
            new Iemas.Domain.Ai.AiUsageRecord { Provider = "OpenRouter", ModelIdentifier = "m", Purpose = "EmailClassification", EmailMessageId = needsAi.Id, Succeeded = false, PromptTokens = 300, CompletionTokens = 0, TotalTokens = 300, CostUsd = 0.0001m },
            new Iemas.Domain.Ai.AiUsageRecord { Provider = "OpenRouter", ModelIdentifier = "m", Purpose = "EmailClassification", EmailMessageId = needsAi.Id, Succeeded = true, PromptTokens = 300, CompletionTokens = 40, TotalTokens = 340, CostUsd = 0.0002m },
            new Iemas.Domain.Ai.AiUsageRecord { Provider = "OpenRouter", ModelIdentifier = "m", Purpose = "ResolutionAssessment", CaseId = c.Id, Succeeded = true, PromptTokens = 500, CompletionTokens = 60, TotalTokens = 560, CostUsd = 0.0004m },
            // someone else's call must never leak in
            new Iemas.Domain.Ai.AiUsageRecord { Provider = "OpenRouter", ModelIdentifier = "m", Purpose = "ResolutionAssessment", CaseId = Guid.NewGuid(), Succeeded = true, PromptTokens = 1, CompletionTokens = 1, TotalTokens = 2, CostUsd = 9m });
        await db.SaveChangesAsync();

        var detail = await new CaseService(db).GetDetailAsync(c.Id, CancellationToken.None);

        var paid = detail!.Emails.Single(e => e.Subject == "Unclear request").AiCost!;
        Assert.Equal(2, paid.Calls);
        Assert.Equal(1, paid.FailedCalls);
        Assert.Equal(600, paid.PromptTokens);
        Assert.Equal(40, paid.CompletionTokens);
        Assert.Equal(0.0003m, paid.CostUsd);
        var free = detail.Emails.Single(e => e.Subject == "Quote please").AiCost!;
        Assert.Equal(0, free.Calls);
        Assert.Equal(0m, free.CostUsd);
        // Our own reply is never screened by AI: it has no cost line at all (not a misleading "$0 / free").
        var reply = detail.Emails.Single(e => e.Subject == "RE: Quote please");
        Assert.Null(reply.AiCost);
        Assert.Equal("Agent", reply.Role);
        Assert.Equal("Customer", detail.Emails.Single(e => e.Subject == "Unclear request").Role);
        var total = detail.AiCost!;
        Assert.Equal(0.0003m, total.Classification.CostUsd);
        Assert.Equal(0.0004m, total.ResolutionChecks.CostUsd);
        Assert.Equal(0.0007m, total.Total.CostUsd);
        Assert.Equal(3, total.Total.Calls);
        Assert.Equal(300 + 340 + 560, total.Total.TotalTokens);   // both classification attempts, plus the resolution check
    }
}
