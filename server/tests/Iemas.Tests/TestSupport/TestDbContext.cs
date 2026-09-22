using Iemas.Application.Common.Interfaces;
using Iemas.Domain.Agents;
using Iemas.Domain.Ai;
using Iemas.Domain.Audit;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Iemas.Domain.Escalations;
using Iemas.Domain.Identity;
using Iemas.Domain.Reminders;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Tests.TestSupport;

/// <summary>
/// Minimal EF InMemory-backed context for Application-layer unit tests. Deliberately does not
/// exercise the Npgsql provider or the LINQ-translation edge cases found in Phase 2 (those are
/// only verified against real PostgreSQL — see IEMAS_Build_Progress_Tracker.md). This context
/// covers pure business-rule logic (validation, cycle detection, encryption round-trips) where
/// InMemory's looser LINQ support is sufficient and faster than a real database.
/// </summary>
public class TestDbContext : DbContext, IAppDbContext
{
    public TestDbContext(DbContextOptions<TestDbContext> options) : base(options)
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserRole>().HasKey(ur => new { ur.UserId, ur.RoleId });

        modelBuilder.Entity<Employee>()
            .HasOne(e => e.Department)
            .WithMany(d => d.Employees)
            .HasForeignKey(e => e.DepartmentId);

        modelBuilder.Entity<Employee>()
            .HasOne(e => e.SupervisorEmployee)
            .WithMany()
            .HasForeignKey(e => e.SupervisorEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Department>()
            .HasOne(d => d.ManagerEmployee)
            .WithMany()
            .HasForeignKey(d => d.ManagerEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<EmailAccount>()
            .HasOne(a => a.Credential)
            .WithOne(c => c.EmailAccount)
            .HasForeignKey<EmailCredential>(c => c.EmailAccountId);

        modelBuilder.Entity<EmailAccount>()
            .HasOne(a => a.OwnerEmployee)
            .WithMany()
            .HasForeignKey(a => a.OwnerEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<EmailMessage>()
            .HasIndex(m => new { m.EmailAccountId, m.ProviderMessageId })
            .IsUnique();

        modelBuilder.Entity<EmailMessage>()
            .HasOne(m => m.EmailAccount)
            .WithMany()
            .HasForeignKey(m => m.EmailAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<EmailAttachmentMetadata>()
            .HasOne(a => a.EmailMessage)
            .WithMany(m => m.Attachments)
            .HasForeignKey(a => a.EmailMessageId);

        modelBuilder.Entity<EmailSyncState>()
            .HasOne(s => s.EmailAccount)
            .WithMany()
            .HasForeignKey(s => s.EmailAccountId);

        modelBuilder.Entity<EmailIntakeLog>()
            .HasOne(l => l.EmailAccount)
            .WithMany()
            .HasForeignKey(l => l.EmailAccountId);

        modelBuilder.Entity<Iemas.Domain.Ai.EmailClassification>()
            .HasIndex(c => c.EmailMessageId)
            .IsUnique();

        modelBuilder.Entity<Iemas.Domain.Ai.EmailClassification>()
            .HasOne(c => c.EmailMessage)
            .WithOne()
            .HasForeignKey<Iemas.Domain.Ai.EmailClassification>(c => c.EmailMessageId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Iemas.Domain.Ai.EmailClassification>()
            .HasOne(c => c.ClassificationProfile)
            .WithMany()
            .HasForeignKey(c => c.ClassificationProfileId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AiClassificationLog>()
            .HasOne(l => l.EmailMessage)
            .WithMany()
            .HasForeignKey(l => l.EmailMessageId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ClassificationProfile>()
            .HasIndex(p => p.Name)
            .IsUnique();

        modelBuilder.Entity<AiModelConfig>()
            .HasIndex(m => new { m.Provider, m.ModelIdentifier })
            .IsUnique();

        modelBuilder.Entity<Case>()
            .HasIndex(c => c.CaseNumber)
            .IsUnique();

        modelBuilder.Entity<Case>()
            .HasOne(c => c.EmailAccount)
            .WithMany()
            .HasForeignKey(c => c.EmailAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Case>()
            .HasOne(c => c.OwnerEmployee)
            .WithMany()
            .HasForeignKey(c => c.OwnerEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CaseEmail>()
            .HasIndex(ce => ce.EmailMessageId)
            .IsUnique();

        modelBuilder.Entity<CaseEmail>()
            .HasOne(ce => ce.Case)
            .WithMany(c => c.Emails)
            .HasForeignKey(ce => ce.CaseId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<CaseEmail>()
            .HasOne(ce => ce.EmailMessage)
            .WithMany()
            .HasForeignKey(ce => ce.EmailMessageId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CaseEvent>()
            .HasOne(e => e.Case)
            .WithMany()
            .HasForeignKey(e => e.CaseId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ReplyVerificationAttempt>()
            .HasOne(a => a.Case)
            .WithMany()
            .HasForeignKey(a => a.CaseId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Agent>()
            .HasIndex(a => a.RegistrationRequestToken)
            .IsUnique();

        modelBuilder.Entity<Agent>()
            .HasOne(a => a.Employee)
            .WithMany()
            .HasForeignKey(a => a.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Agent>()
            .HasOne(a => a.ApprovedByUser)
            .WithMany()
            .HasForeignKey(a => a.ApprovedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Agent>()
            .HasOne(a => a.Credential)
            .WithOne(c => c.Agent)
            .HasForeignKey<AgentCredential>(c => c.AgentId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<AgentCredential>()
            .HasIndex(c => c.KeyHash)
            .IsUnique();

        modelBuilder.Entity<AgentCredential>()
            .HasIndex(c => c.AgentId)
            .IsUnique();

        modelBuilder.Entity<AgentLog>()
            .HasOne(l => l.Agent)
            .WithMany()
            .HasForeignKey(l => l.AgentId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AgentCaseAction>()
            .HasIndex(a => new { a.AgentId, a.RequestId })
            .IsUnique();

        modelBuilder.Entity<AgentCaseAction>()
            .HasOne(a => a.Agent)
            .WithMany()
            .HasForeignKey(a => a.AgentId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AgentCaseAction>()
            .HasOne(a => a.Employee)
            .WithMany()
            .HasForeignKey(a => a.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AgentCaseAction>()
            .HasOne(a => a.Case)
            .WithMany()
            .HasForeignKey(a => a.CaseId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ReminderPolicy>()
            .HasIndex(p => p.Name)
            .IsUnique();

        modelBuilder.Entity<ReminderPolicyHoliday>()
            .HasOne(h => h.ReminderPolicy)
            .WithMany(p => p.Holidays)
            .HasForeignKey(h => h.ReminderPolicyId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Reminder>()
            .HasOne(r => r.Case)
            .WithMany()
            .HasForeignKey(r => r.CaseId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Reminder>()
            .HasOne(r => r.ReminderPolicy)
            .WithMany()
            .HasForeignKey(r => r.ReminderPolicyId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Reminder>()
            .HasIndex(r => r.ExecutionClaimToken)
            .IsUnique();

        // EF InMemory does not support filtered/partial unique indexes (HasFilter, used in the
        // real Npgsql configuration) — enforced here only via the service-layer idempotency check
        // in ReminderSchedulingService (look up by SourceAgentCaseActionId before insert), same as
        // production's DbUpdateException-catch race handling still applies against real Postgres.

        modelBuilder.Entity<EscalationPolicy>()
            .HasIndex(p => p.Name)
            .IsUnique();

        modelBuilder.Entity<EscalationLevel>()
            .HasOne(l => l.EscalationPolicy)
            .WithMany(p => p.Levels)
            .HasForeignKey(l => l.EscalationPolicyId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<EscalationLevel>()
            .HasOne(l => l.SpecificEmployee)
            .WithMany()
            .HasForeignKey(l => l.SpecificEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<EscalationLevel>()
            .HasOne(l => l.SpecificGroup)
            .WithMany()
            .HasForeignKey(l => l.SpecificGroupId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<EscalationGroup>()
            .HasIndex(g => g.Name)
            .IsUnique();

        modelBuilder.Entity<EscalationGroupMember>()
            .HasOne(m => m.EscalationGroup)
            .WithMany(g => g.Members)
            .HasForeignKey(m => m.EscalationGroupId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<EscalationGroupMember>()
            .HasOne(m => m.Employee)
            .WithMany()
            .HasForeignKey(m => m.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<EscalationEvent>()
            .HasOne(e => e.Case)
            .WithMany()
            .HasForeignKey(e => e.CaseId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<EscalationEvent>()
            .HasOne(e => e.EscalationPolicy)
            .WithMany()
            .HasForeignKey(e => e.EscalationPolicyId)
            .OnDelete(DeleteBehavior.Restrict);

        base.OnModelCreating(modelBuilder);
    }

    public static TestDbContext CreateNew()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TestDbContext(options);
    }
}
