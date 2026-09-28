using Iemas.Application.Common.Interfaces;
using Iemas.Domain.Ai;
using Iemas.Domain.Email;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Operations;

public record GenerateSampleDataRequest(Guid EmailAccountId, int Count, bool UseAi);

public record SampleEmailDto(Guid EmailMessageId, string FromAddress, string Subject, string Expected);

public record GenerateSampleDataResult(string Mailbox, List<SampleEmailDto> Emails, bool UseAi);

/// <summary>
/// Puts realistic sample customer emails into a mailbox so the whole flow — Case creation, pop-ups, reply checks,
/// reminders, escalation — can be tried without a real customer writing in. Samples are marked "[Sample]" in the
/// subject and come from example.com addresses; Reset email data removes them like any other email.
/// Without AI they arrive already classified (no OpenRouter cost); with AI they wait for the classifier like real mail.
/// </summary>
public class SampleDataService
{
    public const string SubjectPrefix = "[Sample] ";
    public const int MaxCount = 20;

    private record Template(string Name, string Address, string Subject, string Body, bool NeedsReply, string Category, ClassificationPriority Priority, int? ReplyTo = null);

    /// <summary>The last two are not work (newsletter, auto-reply) so the "Not work" path shows too; #2 is a follow-up to #1.</summary>
    private static readonly Template[] Templates =
    {
        new("Mikko Virtanen", "mikko.virtanen@nordic-spa.example.com", "Quotation request for 12 sauna heaters",
            "Hello,\n\nWe are fitting out a new spa and need a quotation for 12 electric sauna heaters, 9 kW, delivered to Helsinki in March. Could you send prices and delivery times?\n\nBest regards,\nMikko Virtanen\nNordic Spa Oy",
            true, "Quotation", ClassificationPriority.High),
        new("Mikko Virtanen", "mikko.virtanen@nordic-spa.example.com", "RE: Quotation request for 12 sauna heaters",
            "Hello again,\n\nOne more thing: can the heaters come with the wall-mounted control panel instead of the built-in one? Please include that in the quotation.\n\nMikko",
            true, "Quotation", ClassificationPriority.High, ReplyTo: 0),
        new("Anna Schmidt", "a.schmidt@wellness-haus.example.com", "Order 4471 - delivery date?",
            "Dear team,\n\nOur order 4471 was confirmed two weeks ago but we have not received a delivery date yet. Our installation is booked for the 20th. Can you confirm when the goods will ship?\n\nKind regards,\nAnna Schmidt",
            true, "Order status", ClassificationPriority.High),
        new("James Carter", "james.carter@carter-hotels.example.com", "Heater shows error E1 after installation",
            "Hi,\n\nThe heater we installed last week shows error E1 and switches off after a few minutes. The installer checked the wiring. What should we do? Guests are complaining.\n\nJames Carter\nFacilities Manager",
            true, "Technical support", ClassificationPriority.High),
        new("Lucia Rossi", "lucia.rossi@saunamondo.example.com", "Becoming a reseller in Italy",
            "Buongiorno,\n\nWe run three sauna shops in northern Italy and would like to become an authorised reseller. What are the conditions and minimum order quantities?\n\nGrazie,\nLucia Rossi",
            true, "Partnership", ClassificationPriority.Medium),
        new("Peter Nilsson", "peter@nilsson-bygg.example.com", "Invoice 2024-118 appears twice",
            "Hello,\n\nWe received invoice 2024-118 twice with different due dates. Please confirm which one is correct so we can pay.\n\nPeter Nilsson",
            true, "Billing", ClassificationPriority.Medium),
        new("Grace Lim", "grace.lim@spa-asia.example.com", "Wooden bench dimensions for model 30",
            "Hi,\n\nCould you send the dimensions and wood type of the benches for model 30? Our architect needs them for the drawings this week.\n\nThanks,\nGrace",
            true, "Product information", ClassificationPriority.Medium),
        new("Tom Becker", "tom.becker@fitlife.example.com", "Damaged stones in delivery",
            "Hello,\n\nPart of the sauna stones in our last delivery arrived broken. Photos attached in the next email. How do we get a replacement?\n\nTom Becker",
            true, "Complaint", ClassificationPriority.High),
        new("Sauna World Magazine", "news@saunaworld.example.com", "This month: 10 wellness trends",
            "Read our monthly newsletter: the 10 wellness trends of the season, new products and upcoming fairs. Unsubscribe at any time.",
            false, "Newsletter", ClassificationPriority.Low),
        new("Maria Lopez", "maria.lopez@spa-iberia.example.com", "Automatic reply: Out of office",
            "I am out of the office until Monday with limited access to email. For urgent matters please call our office.",
            false, "Auto-reply", ClassificationPriority.Low),
    };

    private readonly IAppDbContext _db;
    private readonly IAuditService _audit;

    public SampleDataService(IAppDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<(GenerateSampleDataResult? Result, string? Error)> GenerateAsync(GenerateSampleDataRequest request, CancellationToken cancellationToken)
    {
        var account = await _db.EmailAccounts.FirstOrDefaultAsync(a => a.Id == request.EmailAccountId, cancellationToken);
        if (account is null || account.Purpose != EmailAccountPurpose.Inbound) return (null, "Choose an inbound mailbox.");
        if (!account.IsActive) return (null, "That mailbox is deactivated — activate it first.");
        if (account.OwnerEmployeeId is null) return (null, "That mailbox has no owner, so nobody would be alerted. Set an owner on Email Accounts first.");

        var count = Math.Clamp(request.Count, 1, MaxCount);
        var now = DateTimeOffset.UtcNow;
        // Received "just now", but never before the mailbox cut-off (e.g. right after a reset), or no Case would be made.
        var earliest = account.ProcessEmailsReceivedAfter is { } cutoff && cutoff > now.AddMinutes(-count) ? cutoff.AddSeconds(1) : now.AddMinutes(-count);

        var created = new List<(EmailMessage Message, Template Template)>();
        for (var i = 0; i < count; i++)
        {
            var template = Templates[i % Templates.Length];
            var round = i / Templates.Length;
            var messageId = $"<sample-{Guid.NewGuid():N}@example.com>";
            var parent = template.ReplyTo is { } replyTo ? created.LastOrDefault(c => c.Template == Templates[replyTo]).Message : null;

            var message = new EmailMessage
            {
                EmailAccountId = account.Id,
                Provider = account.Protocol,
                ProviderMessageId = $"sample-{Guid.NewGuid():N}",
                MessageId = messageId,
                InReplyTo = parent?.MessageId,
                References = parent?.MessageId,
                FromAddress = template.Address,
                FromDisplayName = template.Name,
                ToAddresses = account.EmailAddress,
                Subject = SubjectPrefix + template.Subject + (round > 0 ? $" ({round + 1})" : ""),
                BodyText = template.Body,
                ReceivedAt = Min(earliest.AddSeconds(i * 5), now),
                ProcessingStatus = request.UseAi ? EmailProcessingStatus.PendingClassification : EmailProcessingStatus.Processed,
            };
            _db.EmailMessages.Add(message);
            created.Add((message, template));

            if (!request.UseAi) AddClassification(message, template);
        }

        await _db.SaveChangesAsync(cancellationToken);

        await _audit.LogAsync("SAMPLE_DATA_GENERATED", "EmailAccount", account.Id.ToString(),
            $"{count} sample email(s) added to {account.EmailAddress} ({(request.UseAi ? "sent to the AI" : "pre-classified, no AI cost")}).",
            cancellationToken);

        return (new GenerateSampleDataResult(account.EmailAddress,
            created.Select(c => new SampleEmailDto(c.Message.Id, c.Message.FromAddress, c.Message.Subject,
                c.Template.NeedsReply ? (c.Template.ReplyTo is null ? "Becomes a Case" : "Added to the earlier Case") : "Not work — no Case")).ToList(),
            request.UseAi), null);
    }

    /// <summary>What the AI would have decided, stored the same way a real classification is.</summary>
    private void AddClassification(EmailMessage message, Template template)
    {
        message.Classification = template.Category;
        message.AiConfidence = 0.95;
        message.AiModel = "sample (no AI)";

        _db.EmailClassifications.Add(new Iemas.Domain.Ai.EmailClassification
        {
            EmailMessageId = message.Id,
            Relevance = template.NeedsReply ? ClassificationRelevance.Relevant : ClassificationRelevance.NotRelevant,
            Category = template.Category,
            ActionRequired = template.NeedsReply,
            ResponseExpected = template.NeedsReply,
            Legitimate = template.NeedsReply,
            Priority = template.Priority,
            AiConfidence = 0.95,
            Summary = $"Sample email: {template.Subject}.",
            AiProvider = "Sample data",
            AiModel = "sample (no AI)",
            PromptProfileVersion = "sample",
            ConfidenceBand = ConfidenceBand.High,
            Decision = template.NeedsReply ? ImportanceDecision.Important : ImportanceDecision.NotImportant,
            DecisionReason = template.NeedsReply ? "Sample: customer asks for a reply." : "Sample: not work (newsletter or auto-reply).",
        });
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
