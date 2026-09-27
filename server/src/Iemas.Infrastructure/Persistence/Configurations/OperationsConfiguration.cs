using Iemas.Domain.Notifications;
using Iemas.Domain.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Iemas.Infrastructure.Persistence.Configurations;

public class NotificationTemplateConfiguration : IEntityTypeConfiguration<NotificationTemplate>
{
    public void Configure(EntityTypeBuilder<NotificationTemplate> builder)
    {
        builder.ToTable("notification_templates");
        builder.HasKey(t => t.Id);
        builder.HasIndex(t => t.Type).IsUnique();
        builder.Property(t => t.Title).IsRequired().HasMaxLength(200);
        builder.Property(t => t.MessageText).IsRequired().HasMaxLength(2000);
        builder.Property(t => t.UpdatedByEmail).HasMaxLength(256);
    }
}

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Title).IsRequired().HasMaxLength(200);
        builder.Property(n => n.Message).IsRequired().HasMaxLength(4000);
        builder.Property(n => n.FailureReason).HasMaxLength(1000);
        builder.HasIndex(n => n.CreatedAt);
        builder.HasIndex(n => new { n.CaseId, n.Status });
        builder.HasIndex(n => new { n.EmployeeId, n.CreatedAt });

        builder.HasOne(n => n.Case)
            .WithMany()
            .HasForeignKey(n => n.CaseId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(n => n.Employee)
            .WithMany()
            .HasForeignKey(n => n.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class EmergencyPauseControlConfiguration : IEntityTypeConfiguration<EmergencyPauseControl>
{
    public void Configure(EntityTypeBuilder<EmergencyPauseControl> builder)
    {
        builder.ToTable("emergency_pause_controls");
        builder.HasKey(c => c.Id);
        builder.HasIndex(c => c.Control).IsUnique();
        builder.Property(c => c.Reason).HasMaxLength(500);
        builder.Property(c => c.ChangedByEmail).HasMaxLength(256);
    }
}

public class SystemSettingConfiguration : IEntityTypeConfiguration<SystemSetting>
{
    public void Configure(EntityTypeBuilder<SystemSetting> builder)
    {
        builder.ToTable("system_settings");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Key).IsRequired().HasMaxLength(100);
        builder.HasIndex(s => s.Key).IsUnique();
        builder.Property(s => s.Value).IsRequired().HasMaxLength(1000);
        builder.Property(s => s.UpdatedByEmail).HasMaxLength(256);
    }
}
