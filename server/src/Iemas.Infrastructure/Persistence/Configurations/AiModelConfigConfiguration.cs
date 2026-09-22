using Iemas.Domain.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Iemas.Infrastructure.Persistence.Configurations;

public class AiModelConfigConfiguration : IEntityTypeConfiguration<AiModelConfig>
{
    public void Configure(EntityTypeBuilder<AiModelConfig> builder)
    {
        builder.ToTable("ai_model_configs");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Provider).IsRequired().HasMaxLength(100);
        builder.Property(m => m.ModelIdentifier).IsRequired().HasMaxLength(200);
        builder.Property(m => m.DisplayName).IsRequired().HasMaxLength(200);
        builder.Property(m => m.TaskCapability).IsRequired().HasMaxLength(100);

        builder.HasIndex(m => new { m.Provider, m.ModelIdentifier }).IsUnique();
        builder.HasIndex(m => m.TaskCapability);
    }
}
