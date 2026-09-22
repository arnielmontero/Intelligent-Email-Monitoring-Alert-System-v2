using Iemas.Domain.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Iemas.Infrastructure.Persistence.Configurations;

public class EmailAccountConfiguration : IEntityTypeConfiguration<EmailAccount>
{
    public void Configure(EntityTypeBuilder<EmailAccount> builder)
    {
        builder.ToTable("email_accounts");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.EmailAddress).IsRequired().HasMaxLength(256);
        builder.Property(a => a.Host).IsRequired().HasMaxLength(256);
        builder.Property(a => a.Encryption).IsRequired().HasMaxLength(32);
        builder.Property(a => a.Username).IsRequired().HasMaxLength(256);

        // A given mailbox address can be enrolled at most once per purpose (inbound vs outbound
        // are legitimately separate accounts even if they share an address in edge cases — §19).
        builder.HasIndex(a => new { a.EmailAddress, a.Purpose }).IsUnique();

        builder.HasOne(a => a.OwnerEmployee)
            .WithMany()
            .HasForeignKey(a => a.OwnerEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.Credential)
            .WithOne(c => c.EmailAccount)
            .HasForeignKey<EmailCredential>(c => c.EmailAccountId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class EmailCredentialConfiguration : IEntityTypeConfiguration<EmailCredential>
{
    public void Configure(EntityTypeBuilder<EmailCredential> builder)
    {
        builder.ToTable("email_credentials");
        builder.HasKey(c => c.Id);
        builder.HasIndex(c => c.EmailAccountId).IsUnique();

        builder.Property(c => c.EncryptedSecret).IsRequired();
        builder.Property(c => c.Nonce).IsRequired();
        builder.Property(c => c.Tag).IsRequired();
        builder.Property(c => c.KeyId).IsRequired().HasMaxLength(50);
    }
}
