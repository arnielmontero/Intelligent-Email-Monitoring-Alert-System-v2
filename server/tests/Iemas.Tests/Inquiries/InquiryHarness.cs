using Iemas.Application.Agents;
using Iemas.Application.Cases;
using Iemas.Application.Common.Ai;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Common.Providers;
using Iemas.Application.EmailClassification;
using Iemas.Application.EmailIntake;
using Iemas.Application.Escalations;
using Iemas.Application.Inquiries;
using Iemas.Application.Notifications;
using Iemas.Application.Reminders;
using Iemas.Domain.Ai;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Iemas.Domain.Escalations;
using Iemas.Domain.Identity;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iemas.Tests.Inquiries;

/// <summary>Captures management escalation email instead of sending it.</summary>
public sealed class CapturingEscalationEmailSender : IEscalationEmailSender
{
    public List<OutboundEmail> Sent { get; } = new();

    public Task<OutboundEmailResult> SendAsync(OutboundEmail email, CancellationToken cancellationToken)
    {
        Sent.Add(email);
        return Task.FromResult(new OutboundEmailResult(true, $"<esc-{Sent.Count}@iemas.test>", null));
    }
}

/// <summary>Deterministic stand-in for the AI's reading of a conversation.</summary>
public sealed class FakeResolutionAssessor : IAiResolutionAssessor
{
    public List<ResolutionAssessmentRequest> Requests { get; } = new();

    public Task<ResolutionAssessmentResult> AssessAsync(ResolutionAssessmentRequest request, string modelIdentifier, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var last = request.Turns.LastOrDefault(t => t.Role != "Internal");
        var text = last?.Text.ToLowerInvariant() ?? "";
        var (verdict, confidence) =
            last?.Role == "Customer" && text.Contains("thank") && text.Contains("works") ? (ResolutionVerdict.AppearsResolved, 0.92)
            : last?.Role == "Customer" ? (ResolutionVerdict.CustomerWaiting, 0.9)
            : text.Contains("received your") || text.Contains("look into it") ? (ResolutionVerdict.AcknowledgedOnly, 0.93)
            : (ResolutionVerdict.InProgress, 0.7);
        return Task.FromResult(new ResolutionAssessmentResult(true, verdict, confidence, $"Fake verdict {verdict}.", "Fake", modelIdentifier, null, 1));
    }
}

/// <summary>
/// The whole inquiry pipeline wired with real services over an in-memory database, fake mailboxes
/// (Inbox + Sent per agent) and an explicit clock. Each <see cref="RunAsync"/> is what the scheduled
/// jobs do: intake → screening/AI → Case workflow → Sent-folder sync → supervision engine.
/// </summary>
public sealed class InquiryHarness : IDisposable
{
    /// <summary>Monday 2 Nov 2026, 09:00 UTC. The business calendar is Mon-Fri 08:00-17:00 UTC.</summary>
    public static readonly DateTimeOffset Monday0900 = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private sealed class Mailbox
    {
        public required EmailAccount Account { get; init; }
        public List<(uint Uid, ProviderMessage Message)> Inbox { get; } = new();
        public List<ProviderMessage> Sent { get; } = new();
    }

    private readonly Dictionary<string, Mailbox> _mailboxes = new(StringComparer.OrdinalIgnoreCase);
    private uint _nextUid = 1;
    private int _nextId = 1;

    public TestDbContext Db { get; } = TestDbContext.CreateNew();
    public FakeAgentNotificationDispatcher PopUps { get; } = new();
    public CapturingEscalationEmailSender ManagementEmail { get; } = new();
    public FakeAiClassificationProvider Ai { get; }
    public FakeResolutionAssessor Assessor { get; } = new();

    public Employee Manager { get; }
    public Employee Director { get; }
    public Dictionary<string, Employee> Agents { get; } = new();

    private readonly EmailIntakeService _intake;
    private readonly EmailClassificationService _classification;
    private readonly CaseWorkflowService _workflow;
    private readonly ConversationSyncService _sync;
    public ConversationSyncService Sync => _sync;
    public InquirySupervisionService Supervision { get; }
    public AgentCaseActionService AgentActions { get; }

    public InquiryHarness(params string[] agentMailboxes)
    {
        Manager = new Employee { FullName = "Mia Manager", Email = "mia.manager@sawo.com" };
        Director = new Employee { FullName = "Dan Director", Email = "dan.director@sawo.com" };
        Db.Employees.AddRange(Manager, Director);

        foreach (var address in agentMailboxes.DefaultIfEmpty("agent1@sawo.com"))
        {
            var name = address.Split('@')[0];
            var employee = new Employee { FullName = $"Agent {name}", Email = address, SupervisorEmployeeId = Manager.Id };
            var account = new EmailAccount
            {
                EmailAddress = address, Purpose = EmailAccountPurpose.Inbound, Protocol = EmailProtocol.Imap, Host = "imap.test", Port = 993,
                Encryption = "SSL/TLS", Username = address, AuthMethod = EmailAuthMethod.Password, IsActive = true, MonitoringEnabled = true,
                OwnerEmployeeId = employee.Id,
                Credential = new EmailCredential { EncryptedSecret = System.Text.Encoding.UTF8.GetBytes("pw"), Nonce = Array.Empty<byte>(), Tag = Array.Empty<byte>(), KeyId = "test" },
            };
            Db.Employees.Add(employee);
            Db.EmailAccounts.Add(account);
            Agents[address] = employee;
            _mailboxes[address] = new Mailbox { Account = account };
        }

        Db.AiModelConfigs.Add(new AiModelConfig { Provider = "OpenRouter", ModelIdentifier = "m", DisplayName = "m", Enabled = true, TaskCapability = "EmailClassification", TimeoutSeconds = 30 });

        var policy = new EscalationPolicy { Name = "Default", Enabled = true, IsDefault = true, MaximumLevel = 2, TriggerReminderCount = 2 };
        policy.Levels.Add(new EscalationLevel { EscalationPolicyId = policy.Id, Level = 1, RecipientType = EscalationRecipientType.EmployeeSupervisor });
        policy.Levels.Add(new EscalationLevel { EscalationPolicyId = policy.Id, Level = 2, RecipientType = EscalationRecipientType.SpecificEmployee, SpecificEmployeeId = Director.Id });
        Db.EscalationPolicies.Add(policy);
        Db.SaveChanges();

        // AI classification for whatever the Zettelkasten filter leaves Uncertain: someone asking us
        // something (a question, or interest in a project) is an inquiry; anything else is not.
        Ai = new FakeAiClassificationProvider
        {
            Behavior = (request, model, _, _) =>
            {
                var body = request.BodyText ?? "";
                var relevant = body.Contains('?') || body.Contains("project", StringComparison.OrdinalIgnoreCase);
                return Task.FromResult(new ClassificationAttemptResult(true, "OpenRouter", model,
                    new ClassificationResponse(relevant, relevant ? "PROJECT_INQUIRY" : "OTHER", relevant, relevant, "MEDIUM", 0.93,
                        relevant ? "Prospect asking whether we can support a project." : "Not a customer request.", relevant), null, 3));
            },
        };

        var encryption = new PassThroughEncryptionService();
        var adapter = new FakeEmailProviderAdapter
        {
            FetchBehavior = (settings, _, afterUid, _, _) =>
            {
                var box = _mailboxes[settings.Username];
                var fresh = box.Inbox.Where(m => afterUid is null || m.Uid > afterUid).ToList();
                return Task.FromResult(new FetchInboxResult(1, fresh.Count == 0 ? afterUid : fresh.Max(m => m.Uid),
                    fresh.Select(m => m.Message).ToList(), Array.Empty<(uint, string)>()));
            },
            FetchSentBehavior = (settings, _, _, _) =>
                Task.FromResult(new FetchSentResult(true, null, _mailboxes[settings.Username].Sent.ToList(), Array.Empty<(string, string)>())),
        };
        var adapters = new FakeEmailProviderAdapterResolver(adapter);

        var notifications = new NotificationService(Db, PopUps);
        var conversation = new InquiryConversationService(Db, notifications);
        var protector = new AgentReplyProtector(encryption);
        var reminders = new ReminderSchedulingService(Db);

        _intake = new EmailIntakeService(Db, encryption, adapters, NullLogger<EmailIntakeService>.Instance);
        _classification = new EmailClassificationService(Db, Ai, new NoOpRetryDelay(), new AiCircuitBreakerStore(TimeProvider.System), NullLogger<EmailClassificationService>.Instance);
        _workflow = new CaseWorkflowService(Db, new CaseMatchingService(Db), reminders, notifications, conversation);
        _sync = new ConversationSyncService(Db, encryption, adapters, conversation, protector);
        var escalation = new EscalationService(Db, notifications, ManagementEmail);
        Supervision = new InquirySupervisionService(Db, escalation, notifications, new ResolutionAssessmentService(Db, Assessor, conversation, protector));
        AgentActions = new AgentCaseActionService(Db, _workflow, reminders, conversation);
    }

    /// <summary>A message arrives in an agent's inbox. Returns its Message-ID.</summary>
    public string Receive(string mailbox, string from, string subject, string body, DateTimeOffset at, string? inReplyTo = null, string? fromName = null)
    {
        var id = $"<m{_nextId++}@test>";
        var refs = inReplyTo is null ? Array.Empty<string>() : References(inReplyTo).Append(inReplyTo).Distinct().ToArray();
        var msg = new ProviderMessage((_nextUid).ToString(), id, null, inReplyTo, refs, from, fromName, new[] { mailbox }, Array.Empty<string>(),
            subject, body, null, at, Array.Empty<ProviderAttachmentSummary>());
        _mailboxes[mailbox].Inbox.Add((_nextUid++, msg));
        _known[id] = refs;
        return id;
    }

    /// <summary>The agent sends a message from their own mail client (it lands in their Sent folder). Returns its Message-ID.</summary>
    public string Send(string mailbox, string to, string subject, string body, DateTimeOffset at, string? inReplyTo = null)
    {
        var id = $"<m{_nextId++}@test>";
        var refs = inReplyTo is null ? Array.Empty<string>() : References(inReplyTo).Append(inReplyTo).Distinct().ToArray();
        _mailboxes[mailbox].Sent.Add(new ProviderMessage($"s{_nextId}", id, null, inReplyTo, refs, mailbox, null, new[] { to }, Array.Empty<string>(),
            subject, body, null, at, Array.Empty<ProviderAttachmentSummary>()));
        _known[id] = refs;
        return id;
    }

    private readonly Dictionary<string, string[]> _known = new();
    private IEnumerable<string> References(string parent) => _known.TryGetValue(parent, out var r) ? r : Array.Empty<string>();

    /// <summary>One cycle of every scheduled job, at time <paramref name="now"/>.</summary>
    public async Task<SupervisionRunResult> RunAsync(DateTimeOffset now)
    {
        await _intake.RunAllAsync(CancellationToken.None);
        await _classification.RunAsync(100, CancellationToken.None);
        await _workflow.RunAsync(100, CancellationToken.None);
        await _sync.RunAsync(500, CancellationToken.None);
        return await Supervision.RunAsync(1000, CancellationToken.None, now);
    }

    public async Task<Case> CaseAsync(string mailbox, string customer)
    {
        var accountId = _mailboxes[mailbox].Account.Id;
        Db.ChangeTracker.Clear();
        return await Db.Cases.SingleAsync(c => c.EmailAccountId == accountId && c.CustomerEmailAddress == customer);
    }

    public List<string> PopUpTitlesFor(string mailbox) =>
        PopUps.Sent.Where(p => p.EmployeeId == Agents[mailbox].Id).Select(p => p.Command.Title ?? "").ToList();

    public static DateTimeOffset Day(int dayOffset, int hour, int minute = 0) =>
        Monday0900.Date.AddDays(dayOffset).AddHours(hour).AddMinutes(minute) is var d ? new DateTimeOffset(d, TimeSpan.Zero) : default;

    public void Dispose() => Db.Dispose();
}
