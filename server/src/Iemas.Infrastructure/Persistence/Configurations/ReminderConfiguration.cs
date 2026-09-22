using Iemas.Domain.Reminders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Iemas.Infrastructure.Persistence.Configurations;

public class ReminderPolicyConfiguration : IEntityTypeConfiguration<ReminderPolicy>
{
    public void Configure(EntityTypeBuilder<ReminderPolicy> builder)
    {
        builder.ToTable("reminder_policies");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Name).IsRequired().HasMaxLength(200);
        builder.HasIndex(p => p.Name).IsUnique();

        builder.Property(p => p.TimeZoneId).IsRequired().HasMaxLength(100);

        builder.HasIndex(p => p.ClassificationProfileId);
        builder.HasIndex(p => p.IsDefault);

        // Deliberately no FK to classification_profiles — a policy may reference a profile that
        // is later deleted; resolution falls back to the default policy (mirrors AiModelConfig's
        // own loose coupling to task capability rather than a hard FK to a specific caller).
    }
}

public class ReminderPolicyHolidayConfiguration : IEntityTypeConfiguration<ReminderPolicyHoliday>
{
    public void Configure(EntityTypeBuilder<ReminderPolicyHoliday> builder)
    {
        builder.ToTable("reminder_policy_holidays");
        builder.HasKey(h => h.Id);

        builder.HasIndex(h => new { h.ReminderPolicyId, h.Date }).IsUnique();

        builder.HasOne(h => h.ReminderPolicy)
            .WithMany(p => p.Holidays)
            .HasForeignKey(h => h.ReminderPolicyId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class ReminderConfiguration : IEntityTypeConfiguration<Reminder>
{
    public void Configure(EntityTypeBuilder<Reminder> builder)
    {
        builder.ToTable("reminders");
        builder.HasKey(r => r.Id);

        // §55 recheck query needs these; §78 execution-claim idempotency needs the token index.
        builder.HasIndex(r => new { r.Status, r.ScheduledForUtc });
        builder.HasIndex(r => r.CaseId);
        builder.HasIndex(r => r.ExecutionClaimToken).IsUnique();

        // §56 idempotency — the same Agent RemindLater request must never produce two Reminder rows.
        // No naming convention is configured (see other configs in this project), so EF keeps the
        // PascalCase CLR property name as the column name; the filter must match it exactly.
        builder.HasIndex(r => r.SourceAgentCaseActionId).IsUnique().HasFilter("\"SourceAgentCaseActionId\" IS NOT NULL");

        builder.HasOne(r => r.Case)
            .WithMany()
            .HasForeignKey(r => r.CaseId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.ReminderPolicy)
            .WithMany()
            .HasForeignKey(r => r.ReminderPolicyId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
