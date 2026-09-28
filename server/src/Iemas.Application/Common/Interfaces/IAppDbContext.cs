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

namespace Iemas.Application.Common.Interfaces;

/// <summary>
/// Application-layer abstraction over the EF Core DbContext so Application does not
/// reference Infrastructure (clean module boundaries per requirements §5 architecture).
/// </summary>
public interface IAppDbContext
{
    DbSet<User> Users { get; }
    DbSet<Role> Roles { get; }
    DbSet<UserRole> UserRoles { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<Employee> Employees { get; }
    DbSet<Department> Departments { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<EmailAccount> EmailAccounts { get; }
    DbSet<EmailCredential> EmailCredentials { get; }
    DbSet<EmailMessage> EmailMessages { get; }
    DbSet<EmailAttachmentMetadata> EmailAttachmentMetadata { get; }
    DbSet<EmailSyncState> EmailSyncStates { get; }
    DbSet<EmailIntakeLog> EmailIntakeLogs { get; }
    DbSet<ClassificationProfile> ClassificationProfiles { get; }
    DbSet<AiModelConfig> AiModelConfigs { get; }
    DbSet<Iemas.Domain.Ai.EmailClassification> EmailClassifications { get; }
    DbSet<AiClassificationLog> AiClassificationLogs { get; }
    DbSet<Case> Cases { get; }
    DbSet<CaseEmail> CaseEmails { get; }
    DbSet<CaseEvent> CaseEvents { get; }
    DbSet<ReplyVerificationAttempt> ReplyVerificationAttempts { get; }
    DbSet<Agent> Agents { get; }
    DbSet<AgentCredential> AgentCredentials { get; }
    DbSet<AgentLog> AgentLogs { get; }
    DbSet<AgentCaseAction> AgentCaseActions { get; }
    DbSet<ReminderPolicy> ReminderPolicies { get; }
    DbSet<ReminderPolicyHoliday> ReminderPolicyHolidays { get; }
    DbSet<Reminder> Reminders { get; }
    DbSet<EscalationPolicy> EscalationPolicies { get; }
    DbSet<EscalationLevel> EscalationLevels { get; }
    DbSet<EscalationGroup> EscalationGroups { get; }
    DbSet<EscalationGroupMember> EscalationGroupMembers { get; }
    DbSet<EscalationEvent> EscalationEvents { get; }
    DbSet<NotificationTemplate> NotificationTemplates { get; }
    DbSet<Notification> Notifications { get; }
    DbSet<EmergencyPauseControl> EmergencyPauseControls { get; }
    DbSet<SystemSetting> SystemSettings { get; }
    DbSet<AiProviderConfig> AiProviderConfigs { get; }
    DbSet<AiUsageRecord> AiUsageRecords { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
