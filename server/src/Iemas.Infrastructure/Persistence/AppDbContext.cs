using Iemas.Application.Common.Interfaces;
using Iemas.Domain.Agents;
using Iemas.Domain.Ai;
using Iemas.Domain.Audit;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Iemas.Domain.Escalations;
using Iemas.Domain.Identity;
using Iemas.Domain.Notifications;
using Iemas.Domain.Operations;
using Iemas.Domain.Reminders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Iemas.Infrastructure.Persistence;

public class AppDbContext : DbContext, IAppDbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<EmailAccount> EmailAccounts => Set<EmailAccount>();
    public DbSet<EmailCredential> EmailCredentials => Set<EmailCredential>();
    public DbSet<EmailMessage> EmailMessages => Set<EmailMessage>();
    public DbSet<EmailAttachmentMetadata> EmailAttachmentMetadata => Set<EmailAttachmentMetadata>();
    public DbSet<EmailSyncState> EmailSyncStates => Set<EmailSyncState>();
    public DbSet<EmailIntakeLog> EmailIntakeLogs => Set<EmailIntakeLog>();
    public DbSet<ClassificationProfile> ClassificationProfiles => Set<ClassificationProfile>();
    public DbSet<AiModelConfig> AiModelConfigs => Set<AiModelConfig>();
    public DbSet<Iemas.Domain.Ai.EmailClassification> EmailClassifications => Set<Iemas.Domain.Ai.EmailClassification>();
    public DbSet<AiClassificationLog> AiClassificationLogs => Set<AiClassificationLog>();
    public DbSet<Case> Cases => Set<Case>();
    public DbSet<CaseEmail> CaseEmails => Set<CaseEmail>();
    public DbSet<CaseEvent> CaseEvents => Set<CaseEvent>();
    public DbSet<ReplyVerificationAttempt> ReplyVerificationAttempts => Set<ReplyVerificationAttempt>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AgentCredential> AgentCredentials => Set<AgentCredential>();
    public DbSet<AgentLog> AgentLogs => Set<AgentLog>();
    public DbSet<AgentCaseAction> AgentCaseActions => Set<AgentCaseAction>();
    public DbSet<ReminderPolicy> ReminderPolicies => Set<ReminderPolicy>();
    public DbSet<ReminderPolicyHoliday> ReminderPolicyHolidays => Set<ReminderPolicyHoliday>();
    public DbSet<Reminder> Reminders => Set<Reminder>();
    public DbSet<EscalationPolicy> EscalationPolicies => Set<EscalationPolicy>();
    public DbSet<EscalationLevel> EscalationLevels => Set<EscalationLevel>();
    public DbSet<EscalationGroup> EscalationGroups => Set<EscalationGroup>();
    public DbSet<EscalationGroupMember> EscalationGroupMembers => Set<EscalationGroupMember>();
    public DbSet<EscalationEvent> EscalationEvents => Set<EscalationEvent>();
    public DbSet<NotificationTemplate> NotificationTemplates => Set<NotificationTemplate>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<EmergencyPauseControl> EmergencyPauseControls => Set<EmergencyPauseControl>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Defensive, schema-wide fix (not just the one call site that first hit it): Npgsql only
        // accepts DateTimeOffset values with Offset=0 for timestamptz columns. Any value carrying
        // a non-UTC offset — e.g. a customer email's Date header in their local timezone — throws
        // at save time. Found live via Phase 3 intake verification (see progress tracker bugs
        // log). A global value converter normalizes every DateTimeOffset column on write, so this
        // class of bug cannot resurface at a different call site later.
        var utcConverter = new ValueConverter<DateTimeOffset, DateTimeOffset>(
            v => v.ToUniversalTime(),
            v => v);
        var nullableUtcConverter = new ValueConverter<DateTimeOffset?, DateTimeOffset?>(
            v => v.HasValue ? v.Value.ToUniversalTime() : v,
            v => v);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(utcConverter);
                }
                else if (property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(nullableUtcConverter);
                }
            }
        }

        base.OnModelCreating(modelBuilder);
    }
}
