using Iemas.Domain.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Iemas.Infrastructure.Persistence.Configurations;

public class EmailClassificationConfiguration : IEntityTypeConfiguration<Iemas.Domain.Ai.EmailClassification>
{
    public void Configure(EntityTypeBuilder<Iemas.Domain.Ai.EmailClassification> builder)
    {
        builder.ToTable("email_classifications");
        builder.HasKey(c => c.Id);

        // One classification row per message — a re-run overwrites this row rather than
        // accumulating history (history-of-classification-attempts lives in AiClassificationLog).
        builder.HasIndex(c => c.EmailMessageId).IsUnique();

        builder.HasOne(c => c.EmailMessage)
            .WithOne()
            .HasForeignKey<Iemas.Domain.Ai.EmailClassification>(c => c.EmailMessageId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(c => c.ClassificationProfile)
            .WithMany()
            .HasForeignKey(c => c.ClassificationProfileId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(c => c.Decision);
    }
}
