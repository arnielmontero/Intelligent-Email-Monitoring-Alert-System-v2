using Iemas.Application.Common.Interfaces;
using Iemas.Domain.Agents;
using Iemas.Domain.Ai;
using Iemas.Domain.Email;
using Iemas.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Operations;

/// <summary>Leave <paramref name="EmailAccountId"/> empty to spread the samples at random over every usable mailbox.</summary>
public record GenerateSampleDataRequest(int Count, bool UseAi, Guid? EmailAccountId = null);

public record SampleEmailDto(Guid EmailMessageId, string Mailbox, string FromAddress, string Subject, string Expected);

/// <param name="TeamCreated">What was added to the sample team on this run (empty when it already existed).</param>
public record GenerateSampleDataResult(List<SampleEmailDto> Emails, bool UseAi, List<string> TeamCreated);

/// <summary>
/// Puts realistic sample customer emails into the monitored mailboxes so the whole flow — Case creation, pop-ups,
/// reply checks, reminders, escalation — can be tried without a real customer writing in. Senders, their companies
/// and domains, the email chosen and the mailbox it lands in are random; subjects are marked "[Sample]".
/// Reset email data removes them like any other email. Without AI they arrive already classified (no OpenRouter
/// cost); with AI they wait for the classifier like real mail.
/// The first run also creates a sample team — a "Sample Sales" department with a manager, a supervisor and three
/// staff, each of the staff with a sample mailbox and a sample PC — so Cases spread over several owners and
/// escalation has a real supervisor chain. Everything in it uses @sample.iemas.local or a "Sample" name, which is
/// how Reset email data finds and removes it; real employees, mailboxes and PCs are never touched.
/// </summary>
public class SampleDataService
{
    public const string SubjectPrefix = "[Sample] ";
    public const int MaxCount = 20;

    /// <summary>Addresses of the sample team; ".local" can never be a real internet domain.</summary>
    public const string SampleDomain = "sample.iemas.local";

    /// <summary>Server name of sample mailboxes. ".invalid" never resolves, and reply checks recognise it (see ReplyVerificationService).</summary>
    public const string SampleMailboxHost = "sample.invalid";

    public const string SampleDepartmentName = "Sample Sales";

    private record TeamMember(string FullName, string LocalPart, string? SupervisorLocalPart, bool HasMailboxAndPc);

    /// <summary>Manager → supervisor → staff, so every escalation recipient type has someone to resolve to.</summary>
    private static readonly TeamMember[] Team =
    {
        new("Katarina Lind", "katarina.lind", null, false),
        new("Mark Evans", "mark.evans", "katarina.lind", false),
        new("Liam Andersson", "liam.andersson", "mark.evans", true),
        new("Aino Makinen", "aino.makinen", "mark.evans", true),
        new("Diego Fernandez", "diego.fernandez", "katarina.lind", true),
    };

    /// <summary>{name}, {first} and {company} are filled in per email.</summary>
    private record Template(string Subject, string Body, bool NeedsReply, string Category, ClassificationPriority Priority,
        string? SenderLocalPart = null, bool IsFollowUp = false);

    private static readonly Template Quotation = new("Quotation request for 12 sauna heaters",
        "Hello,\n\nWe are fitting out a new spa and need a quotation for 12 electric sauna heaters, 9 kW, delivered in March. Could you send prices and delivery times?\n\nBest regards,\n{name}\n{company}",
        true, "Quotation", ClassificationPriority.High);

    private static readonly Template QuotationFollowUp = new("RE: Quotation request for 12 sauna heaters",
        "Hello again,\n\nOne more thing: can the heaters come with the wall-mounted control panel instead of the built-in one? Please include that in the quotation.\n\n{first}",
        true, "Quotation", ClassificationPriority.High, IsFollowUp: true);

    /// <summary>Stand-alone emails; eight need a reply, the newsletter and the auto-reply are not work.</summary>
    private static readonly Template[] Singles =
    {
        new("Order {order} - delivery date?",
            "Dear team,\n\nOur order {order} was confirmed two weeks ago but we have not received a delivery date yet. Our installation is booked for the 20th. Can you confirm when the goods will ship?\n\nKind regards,\n{name}\n{company}",
            true, "Order status", ClassificationPriority.High),
        new("Heater shows error E1 after installation",
            "Hi,\n\nThe heater we installed last week shows error E1 and switches off after a few minutes. The installer checked the wiring. What should we do? Guests are complaining.\n\n{name}\nFacilities Manager, {company}",
            true, "Technical support", ClassificationPriority.High),
        new("Becoming a reseller",
            "Hello,\n\nWe run three sauna shops and would like to become an authorised reseller. What are the conditions and minimum order quantities?\n\nThank you,\n{name}\n{company}",
            true, "Partnership", ClassificationPriority.Medium),
        new("Invoice {invoice} appears twice",
            "Hello,\n\nWe received invoice {invoice} twice with different due dates. Please confirm which one is correct so we can pay.\n\n{name}\n{company}",
            true, "Billing", ClassificationPriority.Medium),
        new("Bench dimensions for model 30",
            "Hi,\n\nCould you send the dimensions and wood type of the benches for model 30? Our architect needs them for the drawings this week.\n\nThanks,\n{first}",
            true, "Product information", ClassificationPriority.Medium),
        new("Damaged stones in delivery",
            "Hello,\n\nPart of the sauna stones in our last delivery arrived broken. How do we get a replacement?\n\n{name}\n{company}",
            true, "Complaint", ClassificationPriority.High),
        new("Warranty question for a 2-year-old heater",
            "Good morning,\n\nOne of our heaters stopped heating after two years of use. Is it still under warranty, and how do we send it in?\n\nRegards,\n{name}",
            true, "Warranty", ClassificationPriority.Medium),
        new("This month: 10 wellness trends",
            "Read our monthly newsletter: the 10 wellness trends of the season, new products and upcoming fairs. Unsubscribe at any time.\n\n{company}",
            false, "Newsletter", ClassificationPriority.Low, SenderLocalPart: "newsletter"),
        new("Automatic reply: Out of office",
            "I am out of the office until Monday with limited access to email. For urgent matters please call our office.\n\n{name}",
            false, "Auto-reply", ClassificationPriority.Low),
    };

    private static readonly string[] FirstNames =
        { "Mikko", "Anna", "James", "Lucia", "Peter", "Grace", "Tom", "Maria", "Sofia", "Lars", "Emma", "Jonas", "Chloe", "Ahmed", "Yuki", "Carlos", "Nina", "Oliver", "Elena", "Henrik" };

    private static readonly string[] LastNames =
        { "Virtanen", "Schmidt", "Carter", "Rossi", "Nilsson", "Lim", "Becker", "Lopez", "Novak", "Hansen", "Dubois", "Keller", "Tanaka", "Silva", "Korhonen", "Martin", "Jensen", "Moreau", "Brown", "Weber" };

    private static readonly string[] CompanyWords =
        { "Nordic Spa", "Wellness Haus", "Carter Hotels", "Sauna Mondo", "Nilsson Bygg", "Spa Asia", "FitLife", "Spa Iberia", "Alpine Resorts", "Blue Lagoon Wellness", "Baltic Saunas", "Harbour Hotel Group", "Forest Retreats", "Urban Fitness", "Lakeside Cabins" };

    private static readonly string[] TopLevelDomains = { "com", "de", "fi", "se", "it", "fr", "es", "co.uk", "nl", "net", "eu", "com.au" };

    private readonly IAppDbContext _db;
    private readonly IAuditService _audit;
    private readonly Random _random;

    public SampleDataService(IAppDbContext db, IAuditService audit, Random? random = null)
    {
        _db = db;
        _audit = audit;
        _random = random ?? Random.Shared;
    }

    public async Task<(GenerateSampleDataResult? Result, string? Error)> GenerateAsync(GenerateSampleDataRequest request, CancellationToken cancellationToken)
    {
        var teamCreated = request.EmailAccountId is null ? await EnsureSampleTeamAsync(cancellationToken) : new List<string>();

        // Only mailboxes that are monitored for customer email, active, and owned — otherwise nobody would be alerted.
        var mailboxes = await _db.EmailAccounts
            .Where(a => a.Purpose == EmailAccountPurpose.Inbound && a.IsActive && a.OwnerEmployeeId != null)
            .Where(a => request.EmailAccountId == null || a.Id == request.EmailAccountId)
            .ToListAsync(cancellationToken);
        if (mailboxes.Count == 0)
            return (null, "No active mailbox with an owner. Add a mailbox and set its owner on Email Accounts first.");

        var count = Math.Clamp(request.Count, 1, MaxCount);
        var now = DateTimeOffset.UtcNow;
        var created = new List<(EmailMessage Message, Template Template)>();
        (EmailMessage Message, string Name, string Company, string Address)? openQuotation = null;

        foreach (var template in PickTemplates(count))
        {
            string name, company, address;
            EmailMessage? parent = null;
            EmailAccount mailbox;

            if (template.IsFollowUp && openQuotation is { } quote)
            {
                // The follow-up comes from the same customer, to the same mailbox, as a reply to their first email.
                (parent, name, company, address) = quote;
                mailbox = mailboxes.First(m => m.Id == parent.EmailAccountId);
            }
            else
            {
                (name, company, address) = RandomSender(template.SenderLocalPart);
                mailbox = mailboxes[_random.Next(mailboxes.Count)];
            }

            // Received "just now", but never before the mailbox cut-off (e.g. right after a reset), or no Case would be made.
            var earliest = mailbox.ProcessEmailsReceivedAfter is { } cutoff && cutoff > now.AddMinutes(-count) ? cutoff.AddSeconds(1) : now.AddMinutes(-count);
            var message = new EmailMessage
            {
                EmailAccountId = mailbox.Id,
                Provider = mailbox.Protocol,
                ProviderMessageId = $"sample-{Guid.NewGuid():N}",
                MessageId = $"<sample-{Guid.NewGuid():N}@{address[(address.IndexOf('@') + 1)..]}>",
                InReplyTo = parent?.MessageId,
                References = parent?.MessageId,
                FromAddress = address,
                FromDisplayName = template.SenderLocalPart is null ? name : company,
                ToAddresses = mailbox.EmailAddress,
                Subject = SubjectPrefix + Fill(template.Subject, name, company),
                BodyText = Fill(template.Body, name, company),
                ReceivedAt = Min(earliest.AddSeconds(created.Count * 5), now),
                ProcessingStatus = request.UseAi ? EmailProcessingStatus.PendingClassification : EmailProcessingStatus.Processed,
            };
            _db.EmailMessages.Add(message);
            created.Add((message, template));
            if (template == Quotation) openQuotation = (message, name, company, address);
            if (!request.UseAi) AddClassification(message, template);
        }

        await _db.SaveChangesAsync(cancellationToken);

        var mailboxNames = mailboxes.ToDictionary(m => m.Id, m => m.EmailAddress);
        await _audit.LogAsync("SAMPLE_DATA_GENERATED", "EmailAccount", null,
            $"{count} sample email(s) added to {string.Join(", ", created.Select(c => mailboxNames[c.Message.EmailAccountId]).Distinct())} " +
            $"({(request.UseAi ? "sent to the AI" : "pre-classified, no AI cost")}).",
            cancellationToken);

        return (new GenerateSampleDataResult(
            created.Select(c => new SampleEmailDto(c.Message.Id, mailboxNames[c.Message.EmailAccountId], c.Message.FromAddress, c.Message.Subject,
                !c.Template.NeedsReply ? "Not work — no Case" : c.Template.IsFollowUp ? "Added to the earlier quotation Case" : "Becomes a Case")).ToList(),
            request.UseAi, teamCreated), null);
    }

    /// <summary>Creates whatever part of the sample team is missing; running it again changes nothing.</summary>
    private async Task<List<string>> EnsureSampleTeamAsync(CancellationToken cancellationToken)
    {
        var created = new List<string>();
        var now = DateTimeOffset.UtcNow;

        var department = await _db.Departments.FirstOrDefaultAsync(d => d.Name == SampleDepartmentName, cancellationToken);
        if (department is null)
        {
            department = new Department { Name = SampleDepartmentName, Description = "Created by Generate sample data." };
            _db.Departments.Add(department);
            created.Add($"department {SampleDepartmentName}");
            // Saved on its own: the department's manager and the employees' department point at each other.
            await _db.SaveChangesAsync(cancellationToken);
        }

        var emails = Team.Select(m => $"{m.LocalPart}@{SampleDomain}").ToList();
        var employees = await _db.Employees.Where(e => emails.Contains(e.Email)).ToDictionaryAsync(e => e.Email, cancellationToken);
        foreach (var member in Team)
        {
            var email = $"{member.LocalPart}@{SampleDomain}";
            if (employees.ContainsKey(email)) continue;
            var employee = new Employee { FullName = member.FullName, Email = email, DepartmentId = department.Id };
            _db.Employees.Add(employee);
            employees[email] = employee;
            created.Add($"employee {member.FullName}");
        }
        foreach (var member in Team.Where(m => m.SupervisorLocalPart is not null))
        {
            var employee = employees[$"{member.LocalPart}@{SampleDomain}"];
            employee.SupervisorEmployeeId ??= employees[$"{member.SupervisorLocalPart}@{SampleDomain}"].Id;
        }
        await _db.SaveChangesAsync(cancellationToken);

        department.ManagerEmployeeId ??= employees[$"{Team[0].LocalPart}@{SampleDomain}"].Id;

        foreach (var member in Team.Where(m => m.HasMailboxAndPc))
        {
            var email = $"{member.LocalPart}@{SampleDomain}";
            var employee = employees[email];
            if (!await _db.EmailAccounts.AnyAsync(a => a.EmailAddress == email, cancellationToken))
            {
                _db.EmailAccounts.Add(new EmailAccount
                {
                    EmailAddress = email,
                    DisplayName = member.FullName,
                    Purpose = EmailAccountPurpose.Inbound,
                    Protocol = EmailProtocol.Imap,
                    Host = SampleMailboxHost,
                    Port = 993,
                    Username = email,
                    AuthMethod = EmailAuthMethod.Password,
                    OwnerEmployeeId = employee.Id,
                    // No real server behind it, so it is never checked for new email; samples are put in directly.
                    MonitoringEnabled = false,
                    IsActive = true,
                    ProcessEmailsReceivedAfter = now.AddDays(-1),
                });
                created.Add($"mailbox {email}");
            }

            var pcName = $"SAMPLE-PC-{member.LocalPart.Split('.')[0].ToUpperInvariant()}";
            if (!await _db.Agents.AnyAsync(a => a.ClientName == pcName, cancellationToken))
            {
                _db.Agents.Add(new Agent
                {
                    ClientName = pcName,
                    EnrollmentEmailAddress = email,
                    // Must be unique; random and never shown, so nobody can collect a credential for a sample PC.
                    RegistrationRequestToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
                    EmployeeId = employee.Id,
                    RegistrationStatus = AgentRegistrationStatus.Approved,
                    ApprovedAt = now,
                    ConnectionStatus = AgentConnectionStatus.Disconnected,
                    AgentVersion = "sample",
                    DeviceMetadata = "Sample PC — not a real computer; its pop-ups stay queued.",
                });
                created.Add($"PC {pcName}");
            }
        }
        await _db.SaveChangesAsync(cancellationToken);
        return created;
    }

    /// <summary>A random mix; the quotation follow-up only ever comes after its quotation.</summary>
    private List<Template> PickTemplates(int count)
    {
        var picked = new List<Template>();
        while (picked.Count < count)
        {
            var pool = Singles.Append(Quotation).OrderBy(_ => _random.Next()).ToList();
            foreach (var template in pool)
            {
                if (picked.Count == count) break;
                picked.Add(template);
                if (template == Quotation && picked.Count < count) picked.Add(QuotationFollowUp);
            }
        }
        return picked;
    }

    private (string Name, string Company, string Address) RandomSender(string? localPart)
    {
        var first = FirstNames[_random.Next(FirstNames.Length)];
        var last = LastNames[_random.Next(LastNames.Length)];
        var company = CompanyWords[_random.Next(CompanyWords.Length)];
        var domain = $"{new string(company.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray())}.{TopLevelDomains[_random.Next(TopLevelDomains.Length)]}";
        var local = localPart ?? $"{first}.{last}".ToLowerInvariant();
        return ($"{first} {last}", company, $"{local}@{domain}");
    }

    private string Fill(string text, string name, string company) => text
        .Replace("{name}", name)
        .Replace("{first}", name.Split(' ')[0])
        .Replace("{company}", company)
        .Replace("{order}", _random.Next(4000, 9999).ToString())
        .Replace("{invoice}", $"{DateTime.UtcNow.Year}-{_random.Next(100, 999)}");

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
            Summary = $"Sample email: {message.Subject[SubjectPrefix.Length..]}.",
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
