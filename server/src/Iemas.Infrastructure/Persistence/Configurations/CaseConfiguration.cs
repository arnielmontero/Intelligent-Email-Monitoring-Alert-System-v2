using Iemas.Domain.Cases;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Iemas.Infrastructure.Persistence.Configurations;

public class CaseConfiguration : IEntityTypeConfiguration<Case>
{
    public void Configure(EntityTypeBuilder<Case> builder)
    {
        builder.ToTable("cases");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.CaseNumber).IsRequired().HasMaxLength(32);
        builder.HasIndex(c => c.CaseNumber).IsUnique();

        builder.Property(c => c.CustomerEmailAddress).IsRequired().HasMaxLength(320);
        builder.Property(c => c.Subject).IsRequired();
        builder.Property(c => c.NormalizedSubject).IsRequired();

        // Requirements §34 — Case Matching needs to query these directly, and the CMS/API needs
        // to filter by status and owner (§88 Search/Filters).
        builder.HasIndex(c => c.CustomerEmailAddress);
        builder.HasIndex(c => c.NormalizedSubject);
        builder.HasIndex(c => c.WorkStatus);
        builder.HasIndex(c => c.OwnerEmployeeId);
        builder.HasIndex(c => new { c.EmailAccountId, c.CustomerEmailAddress });

        builder.HasOne(c => c.EmailAccount)
            .WithMany()
            .HasForeignKey(c => c.EmailAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(c => c.OwnerEmployee)
            .WithMany()
            .HasForeignKey(c => c.OwnerEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class CaseEmailConfiguration : IEntityTypeConfiguration<CaseEmail>
{
    public void Configure(EntityTypeBuilder<CaseEmail> builder)
    {
        builder.ToTable("case_emails");
        builder.HasKey(ce => ce.Id);

        // Requirements §22/§78 — a given email message belongs to exactly one Case; re-matching
        // the same message must be idempotent, not create a second linkage row.
        builder.HasIndex(ce => ce.EmailMessageId).IsUnique();
        builder.HasIndex(ce => ce.CaseId);

        builder.HasOne(ce => ce.Case)
            .WithMany(c => c.Emails)
            .HasForeignKey(ce => ce.CaseId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(ce => ce.EmailMessage)
            .WithMany()
            .HasForeignKey(ce => ce.EmailMessageId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class CaseEventConfiguration : IEntityTypeConfiguration<CaseEvent>
{
    public void Configure(EntityTypeBuilder<CaseEvent> builder)
    {
        builder.ToTable("case_events");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Detail).IsRequired();

        builder.HasIndex(e => e.CaseId);
        builder.HasIndex(e => e.OccurredAt);

        builder.HasOne(e => e.Case)
            .WithMany()
            .HasForeignKey(e => e.CaseId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class ReplyVerificationAttemptConfiguration : IEntityTypeConfiguration<ReplyVerificationAttempt>
{
    public void Configure(EntityTypeBuilder<ReplyVerificationAttempt> builder)
    {
        builder.ToTable("reply_verification_attempts");
        builder.HasKey(a => a.Id);

        // §89 investigation — every attempt for a Case, in order, is the auditable trail.
        builder.HasIndex(a => a.CaseId);
        builder.HasIndex(a => a.AttemptedAt);

        builder.HasOne(a => a.Case)
            .WithMany()
            .HasForeignKey(a => a.CaseId)
            .OnDelete(DeleteBehavior.Cascade);

        // Deliberately no FK constraint to email_messages — the matched Sent message is read
        // live from the mailbox via IMAP, not persisted as an EmailMessage row (Sent items are
        // not part of the Phase 3 intake pipeline). This column is an identifier for investigation
        // only, tied to the message's own Guid derived from its ProviderMessageId at verification
        // time, not a foreign key into a table Sent messages don't live in.
    }
}
