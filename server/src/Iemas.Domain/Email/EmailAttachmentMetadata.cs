using Iemas.Domain.Common;

namespace Iemas.Domain.Email;

/// <summary>
/// Requirements §31 — metadata only, never content. Attachment bytes are deliberately never
/// fetched or stored (security, privacy, malware, storage, AI cost — all explicitly cited reasons
/// not to implement attachment content handling until approved).
/// </summary>
public class EmailAttachmentMetadata : Entity
{
    public Guid EmailMessageId { get; set; }
    public EmailMessage EmailMessage { get; set; } = null!;

    public string FileName { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public long SizeBytes { get; set; }
}
