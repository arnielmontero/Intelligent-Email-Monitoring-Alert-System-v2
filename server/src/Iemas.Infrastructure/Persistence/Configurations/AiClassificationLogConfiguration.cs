using Iemas.Domain.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Iemas.Infrastructure.Persistence.Configurations;

public class AiClassificationLogConfiguration : IEntityTypeConfiguration<AiClassificationLog>
{
    public void Configure(EntityTypeBuilder<AiClassificationLog> builder)
    {
        builder.ToTable("ai_classification_logs");
        builder.HasKey(l => l.Id);

        builder.HasOne(l => l.EmailMessage)
            .WithMany()
            .HasForeignKey(l => l.EmailMessageId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(l => l.EmailMessageId);
    }
}
