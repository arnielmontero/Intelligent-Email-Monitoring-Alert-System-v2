using Iemas.Domain.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Iemas.Infrastructure.Persistence.Configurations;

public class EmailSyncStateConfiguration : IEntityTypeConfiguration<EmailSyncState>
{
    public void Configure(EntityTypeBuilder<EmailSyncState> builder)
    {
        builder.ToTable("email_sync_states");
        builder.HasKey(s => s.Id);
        builder.HasIndex(s => s.EmailAccountId).IsUnique();

        builder.HasOne(s => s.EmailAccount)
            .WithMany()
            .HasForeignKey(s => s.EmailAccountId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class EmailIntakeLogConfiguration : IEntityTypeConfiguration<EmailIntakeLog>
{
    public void Configure(EntityTypeBuilder<EmailIntakeLog> builder)
    {
        builder.ToTable("email_intake_logs");
        builder.HasKey(l => l.Id);
        builder.HasIndex(l => l.CreatedAt);
        builder.HasIndex(l => new { l.EmailAccountId, l.CreatedAt });

        builder.HasOne(l => l.EmailAccount)
            .WithMany()
            .HasForeignKey(l => l.EmailAccountId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
