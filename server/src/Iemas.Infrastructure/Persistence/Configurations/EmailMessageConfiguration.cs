using Iemas.Domain.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Iemas.Infrastructure.Persistence.Configurations;

public class EmailMessageConfiguration : IEntityTypeConfiguration<EmailMessage>
{
    public void Configure(EntityTypeBuilder<EmailMessage> builder)
    {
        builder.ToTable("email_messages");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.ProviderMessageId).IsRequired().HasMaxLength(512);
        builder.Property(m => m.FromAddress).IsRequired().HasMaxLength(320);
        builder.Property(m => m.ToAddresses).IsRequired();
        builder.Property(m => m.Subject).IsRequired();

        // Requirements §22 — duplicate protection is enforced at the database level, not just
        // in application logic, so a race between two intake runs cannot create duplicates.
        builder.HasIndex(m => new { m.EmailAccountId, m.ProviderMessageId }).IsUnique();

        // Requirements §34 — Case Matching needs to query by thread/reply identifiers directly.
        builder.HasIndex(m => m.MessageId);
        builder.HasIndex(m => m.ThreadId);
        builder.HasIndex(m => m.InReplyTo);
        builder.HasIndex(m => m.ReceivedAt);
        builder.HasIndex(m => m.ProcessingStatus);

        builder.HasOne(m => m.EmailAccount)
            .WithMany()
            .HasForeignKey(m => m.EmailAccountId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class EmailAttachmentMetadataConfiguration : IEntityTypeConfiguration<EmailAttachmentMetadata>
{
    public void Configure(EntityTypeBuilder<EmailAttachmentMetadata> builder)
    {
        builder.ToTable("email_attachment_metadata");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.FileName).IsRequired().HasMaxLength(512);

        builder.HasOne(a => a.EmailMessage)
            .WithMany(m => m.Attachments)
            .HasForeignKey(a => a.EmailMessageId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
