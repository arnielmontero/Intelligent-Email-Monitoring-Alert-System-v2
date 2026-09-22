using Iemas.Domain.Common;

namespace Iemas.Domain.Email;

/// <summary>
/// Requirements §21 — normalized email storage. Covers exactly the "minimum information" list;
/// Case/Classification linkage fields are nullable because Phase 3 (this entity) runs before
/// Phase 4 (AI) and Phase 5 (Cases) exist.
///
/// Case matching (§34) explicitly forbids matching by subject alone, so ThreadId/InReplyTo/
/// References are first-class columns here, not buried in a JSON blob — Phase 5's Case Matching
/// will query them directly.
/// </summary>
public class EmailMessage : Entity
{
    public Guid EmailAccountId { get; set; }
    public EmailAccount EmailAccount { get; set; } = null!;

    public EmailProtocol Provider { get; set; }

    /// <summary>Provider-native message identifier (e.g. IMAP UID). Used for duplicate protection (§22).</summary>
    public string ProviderMessageId { get; set; } = string.Empty;

    /// <summary>RFC 5322 Message-ID header, when present. Distinct from ProviderMessageId (§21).</summary>
    public string? MessageId { get; set; }

    public string? ThreadId { get; set; }
    public string? InReplyTo { get; set; }

    /// <summary>RFC 5322 References header, stored as a single space-joined string (order-preserving).</summary>
    public string? References { get; set; }

    public string FromAddress { get; set; } = string.Empty;
    public string? FromDisplayName { get; set; }

    /// <summary>Semicolon-joined address list. Kept simple for Phase 3; §96 says CC/BCC caveats apply.</summary>
    public string ToAddresses { get; set; } = string.Empty;
    public string? CcAddresses { get; set; }

    public string Subject { get; set; } = string.Empty;

    public string? BodyText { get; set; }
    public string? BodyHtml { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    public int AttachmentCount { get; set; }

    public EmailProcessingStatus ProcessingStatus { get; set; } = EmailProcessingStatus.PendingClassification;
    public string? ProcessingError { get; set; }

    // AI classification / Case linkage — populated by Phase 4/5, nullable until then.
    public string? Classification { get; set; }
    public double? AiConfidence { get; set; }
    public string? AiModel { get; set; }
    public Guid? CaseId { get; set; }

    public ICollection<EmailAttachmentMetadata> Attachments { get; set; } = new List<EmailAttachmentMetadata>();
}
