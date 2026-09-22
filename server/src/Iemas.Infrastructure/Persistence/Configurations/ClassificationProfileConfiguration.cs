using Iemas.Domain.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Iemas.Infrastructure.Persistence.Configurations;

public class ClassificationProfileConfiguration : IEntityTypeConfiguration<ClassificationProfile>
{
    public void Configure(EntityTypeBuilder<ClassificationProfile> builder)
    {
        builder.ToTable("classification_profiles");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Name).IsRequired().HasMaxLength(200);
        builder.HasIndex(p => p.Name).IsUnique();

        builder.Property(p => p.Categories).IsRequired();
        builder.Property(p => p.IncludeDefinitions).IsRequired();
        builder.Property(p => p.ExcludeDefinitions).IsRequired();
    }
}
