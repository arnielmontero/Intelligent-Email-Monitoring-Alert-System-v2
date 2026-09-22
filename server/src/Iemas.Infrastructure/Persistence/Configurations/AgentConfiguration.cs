using Iemas.Domain.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Iemas.Infrastructure.Persistence.Configurations;

public class AgentConfiguration : IEntityTypeConfiguration<Agent>
{
    public void Configure(EntityTypeBuilder<Agent> builder)
    {
        builder.ToTable("agents");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.ClientName).IsRequired().HasMaxLength(200);
        builder.Property(a => a.EnrollmentEmailAddress).IsRequired().HasMaxLength(320);
        builder.Property(a => a.RegistrationRequestToken).IsRequired();

        // §68 — the collection token is the only thing an unauthenticated pending Agent can look
        // itself up by; must be unique so a random guess never leaks another Agent's status.
        builder.HasIndex(a => a.RegistrationRequestToken).IsUnique();

        builder.HasIndex(a => a.RegistrationStatus);
        builder.HasIndex(a => a.EmployeeId);

        builder.HasOne(a => a.Employee)
            .WithMany()
            .HasForeignKey(a => a.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.ApprovedByUser)
            .WithMany()
            .HasForeignKey(a => a.ApprovedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.Credential)
            .WithOne(c => c.Agent)
            .HasForeignKey<AgentCredential>(c => c.AgentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class AgentCredentialConfiguration : IEntityTypeConfiguration<AgentCredential>
{
    public void Configure(EntityTypeBuilder<AgentCredential> builder)
    {
        builder.ToTable("agent_credentials");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.KeyHash).IsRequired();
        builder.HasIndex(c => c.KeyHash).IsUnique();
        builder.HasIndex(c => c.AgentId).IsUnique();
    }
}

public class AgentLogConfiguration : IEntityTypeConfiguration<AgentLog>
{
    public void Configure(EntityTypeBuilder<AgentLog> builder)
    {
        builder.ToTable("agent_logs");
        builder.HasKey(l => l.Id);

        builder.HasIndex(l => l.AgentId);
        builder.HasIndex(l => l.OccurredAt);

        builder.HasOne(l => l.Agent)
            .WithMany()
            .HasForeignKey(l => l.AgentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class AgentCaseActionConfiguration : IEntityTypeConfiguration<AgentCaseAction>
{
    public void Configure(EntityTypeBuilder<AgentCaseAction> builder)
    {
        builder.ToTable("agent_case_actions");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.RequestId).IsRequired().HasMaxLength(200);

        // §73/§78 — idempotency: the same Agent replaying the same RequestId must never create a
        // second action/CaseEvent.
        builder.HasIndex(a => new { a.AgentId, a.RequestId }).IsUnique();
        builder.HasIndex(a => a.CaseId);

        builder.HasOne(a => a.Agent)
            .WithMany()
            .HasForeignKey(a => a.AgentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.Employee)
            .WithMany()
            .HasForeignKey(a => a.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.Case)
            .WithMany()
            .HasForeignKey(a => a.CaseId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
