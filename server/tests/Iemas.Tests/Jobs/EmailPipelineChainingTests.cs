using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Iemas.Api.Jobs;
using Iemas.Application.Cases;
using Iemas.Application.Common.Ai;
using Iemas.Application.Common.Providers;
using Iemas.Application.EmailClassification;
using Iemas.Application.EmailIntake;
using Iemas.Application.Escalations;
using Iemas.Application.Reminders;
using Iemas.Domain.Ai;
using Iemas.Domain.Email;
using Iemas.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Iemas.Tests.Jobs;

/// <summary>
/// A customer email should reach its owner as soon as possible: new email found by intake goes straight on to
/// classification, and important email straight on to Case creation (which sends the pop-up), instead of
/// each step waiting for its own timer.
/// </summary>
public class EmailPipelineChainingTests
{
    private sealed class RecordingJobClient : IBackgroundJobClient
    {
        public List<string> Enqueued { get; } = new();

        public string Create(Job job, IState state)
        {
            Enqueued.Add(job.Method.Name);
            return Guid.NewGuid().ToString();
        }

        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }

    private static EmailAccount CreateAccount() => new()
    {
        EmailAddress = "sales@sawo.com", Purpose = EmailAccountPurpose.Inbound, Protocol = EmailProtocol.Imap,
        Host = "imap.example.com", Port = 993, Username = "sales@sawo.com", AuthMethod = EmailAuthMethod.Password,
        IsActive = true, MonitoringEnabled = true,
        Credential = new EmailCredential
        {
            EncryptedSecret = System.Text.Encoding.UTF8.GetBytes("password"), Nonce = Array.Empty<byte>(), Tag = Array.Empty<byte>(), KeyId = "test",
        },
    };

    private static RecurringJobGuards CreateGuards(TestDbContext db, RecordingJobClient jobs, FakeEmailProviderAdapter? adapter = null, bool important = true)
    {
        var provider = new FakeAiClassificationProvider
        {
            Behavior = (_, model, _, _) => Task.FromResult(new ClassificationAttemptResult(
                true, "OpenRouter", model,
                new ClassificationResponse(important, "PRODUCT_INQUIRY", important, important, "HIGH", 0.96, "Customer requesting pricing."),
                null, 5)),
        };
        return new RecurringJobGuards(
            new ReminderExecutionService(db, new ReminderSchedulingService(db)),
            new EscalationService(db),
            new EmailIntakeService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter ?? new FakeEmailProviderAdapter()), NullLogger<EmailIntakeService>.Instance),
            new EmailClassificationService(db, provider, new NoOpRetryDelay(), new AiCircuitBreakerStore(TimeProvider.System), NullLogger<EmailClassificationService>.Instance),
            new CaseWorkflowService(db, new CaseMatchingService(db), new ReminderSchedulingService(db)),
            jobs,
            new ConfigurationBuilder().Build(),
            NullLogger<RecurringJobGuards>.Instance);
    }

    [Fact]
    public async Task Intake_StoringNewEmail_StartsClassificationImmediately()
    {
        using var db = TestDbContext.CreateNew();
        db.EmailAccounts.Add(CreateAccount());
        await db.SaveChangesAsync();
        var adapter = new FakeEmailProviderAdapter
        {
            FetchBehavior = (_, _, _, _, _) => Task.FromResult(new FetchInboxResult(1, 1, new[]
            {
                new ProviderMessage("101", "<101@example.com>", null, null, Array.Empty<string>(), "customer@example.com", "Customer",
                    new[] { "sales@sawo.com" }, Array.Empty<string>(), "Price request", "Please send prices.", null,
                    DateTimeOffset.UtcNow, Array.Empty<ProviderAttachmentSummary>()),
            }, Array.Empty<(uint, string)>())),
        };
        var jobs = new RecordingJobClient();

        await CreateGuards(db, jobs, adapter).RunEmailIntakeAsync(CancellationToken.None);

        Assert.Equal(new[] { nameof(RecurringJobGuards.RunEmailClassificationAsync) }, jobs.Enqueued);
    }

    [Fact]
    public async Task Intake_WithNothingNew_QueuesNothing()
    {
        using var db = TestDbContext.CreateNew();
        db.EmailAccounts.Add(CreateAccount());
        await db.SaveChangesAsync();
        var adapter = new FakeEmailProviderAdapter
        {
            FetchBehavior = (_, _, _, _, _) => Task.FromResult(new FetchInboxResult(1, 0, Array.Empty<ProviderMessage>(), Array.Empty<(uint, string)>())),
        };
        var jobs = new RecordingJobClient();

        await CreateGuards(db, jobs, adapter).RunEmailIntakeAsync(CancellationToken.None);

        Assert.Empty(jobs.Enqueued);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Classification_StartsCaseCreationImmediately_OnlyForImportantEmail(bool important)
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        db.AiModelConfigs.Add(new AiModelConfig
        {
            Provider = "OpenRouter", ModelIdentifier = "openai/gpt-4o-mini", DisplayName = "gpt-4o-mini", Enabled = true,
            TaskCapability = "EmailClassification", TimeoutSeconds = 30, MaxRetries = 0, FallbackOrder = 0,
        });
        db.EmailMessages.Add(new EmailMessage
        {
            EmailAccountId = account.Id, Provider = EmailProtocol.Imap, ProviderMessageId = "1", FromAddress = "customer@example.com",
            ToAddresses = "sales@sawo.com", Subject = "Price request", BodyText = "Please send prices.", ReceivedAt = DateTimeOffset.UtcNow,
            ProcessingStatus = EmailProcessingStatus.PendingClassification,
        });
        await db.SaveChangesAsync();
        var jobs = new RecordingJobClient();

        await CreateGuards(db, jobs, important: important).RunEmailClassificationAsync(25, CancellationToken.None);

        if (important) Assert.Equal(new[] { nameof(RecurringJobGuards.RunCaseWorkflowAsync) }, jobs.Enqueued);
        else Assert.Empty(jobs.Enqueued);
    }
}
