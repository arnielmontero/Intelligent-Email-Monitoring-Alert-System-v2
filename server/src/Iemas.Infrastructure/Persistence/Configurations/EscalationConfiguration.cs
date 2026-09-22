using Iemas.Domain.Escalations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Iemas.Infrastructure.Persistence.Configurations;

public class EscalationPolicyConfiguration : IEntityTypeConfiguration<EscalationPolicy>
{
    public void Configure(EntityTypeBuilder<EscalationPolicy> builder)
    {
        builder.ToTable("escalation_policies");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Name).IsRequired().HasMaxLength(200);
        builder.HasIndex(p => p.Name).IsUnique();

        builder.Property(p => p.Channel).IsRequired().HasMaxLength(50);

        builder.HasIndex(p => p.ClassificationProfileId);
        builder.HasIndex(p => p.IsDefault);

        // No hard FK to classification_profiles — same deliberate loose coupling as ReminderPolicy.
    }
}

public class EscalationLevelConfiguration : IEntityTypeConfiguration<EscalationLevel>
{
    public void Configure(EntityTypeBuilder<EscalationLevel> builder)
    {
        builder.ToTable("escalation_levels");
        builder.HasKey(l => l.Id);

        builder.HasIndex(l => new { l.EscalationPolicyId, l.Level }).IsUnique();

        builder.HasOne(l => l.EscalationPolicy)
            .WithMany(p => p.Levels)
            .HasForeignKey(l => l.EscalationPolicyId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(l => l.SpecificEmployee)
            .WithMany()
            .HasForeignKey(l => l.SpecificEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(l => l.SpecificGroup)
            .WithMany()
            .HasForeignKey(l => l.SpecificGroupId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class EscalationGroupConfiguration : IEntityTypeConfiguration<EscalationGroup>
{
    public void Configure(EntityTypeBuilder<EscalationGroup> builder)
    {
        builder.ToTable("escalation_groups");
        builder.HasKey(g => g.Id);

        builder.Property(g => g.Name).IsRequired().HasMaxLength(200);
        builder.HasIndex(g => g.Name).IsUnique();
    }
}

public class EscalationGroupMemberConfiguration : IEntityTypeConfiguration<EscalationGroupMember>
{
    public void Configure(EntityTypeBuilder<EscalationGroupMember> builder)
    {
        builder.ToTable("escalation_group_members");
        builder.HasKey(m => m.Id);

        builder.HasIndex(m => new { m.EscalationGroupId, m.EmployeeId }).IsUnique();

        builder.HasOne(m => m.EscalationGroup)
            .WithMany(g => g.Members)
            .HasForeignKey(m => m.EscalationGroupId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(m => m.Employee)
            .WithMany()
            .HasForeignKey(m => m.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class EscalationEventConfiguration : IEntityTypeConfiguration<EscalationEvent>
{
    public void Configure(EntityTypeBuilder<EscalationEvent> builder)
    {
        builder.ToTable("escalation_events");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Trigger).IsRequired();

        builder.HasIndex(e => e.CaseId);
        builder.HasIndex(e => e.OccurredAt);

        // §60.8 "Escalation level has not already executed" idempotency guard. Deliberately not a
        // DB-level unique constraint (unlike Reminder's ExecutionClaimToken/SourceAgentCaseActionId
        // indexes) because a Skipped attempt (e.g. ThresholdNotReached, re-evaluated every poll) is
        // expected to recur many times for the same (CaseId, EscalationPolicyId, Level) and must
        // not collide — the "already executed" check in EscalationService instead queries for an
        // existing row with Outcome != Skipped, enforced in application code the same way
        // CaseWorkflowService enforces "already linked" via a query-then-DbUpdateException-catch
        // pattern rather than every possible business rule needing its own DB constraint.
        builder.HasIndex(e => new { e.CaseId, e.EscalationPolicyId, e.Level, e.Outcome });

        builder.HasOne(e => e.Case)
            .WithMany()
            .HasForeignKey(e => e.CaseId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.EscalationPolicy)
            .WithMany()
            .HasForeignKey(e => e.EscalationPolicyId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
