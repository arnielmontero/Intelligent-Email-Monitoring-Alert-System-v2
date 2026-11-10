using Iemas.Application.Common.Providers;

namespace Iemas.Application.Agents.Dtos;

/// <summary>
/// One email from the employee's Outlook Sent Items, reported by the Windows Agent. Used when Outlook keeps
/// sent mail only on the PC (POP accounts), so the server's Sent folder never sees the agent's replies.
/// </summary>
public record AgentSentMessageDto(
    string EntryId,
    string? MessageId,
    string? InReplyTo,
    string? References,
    string From,
    string? FromName,
    List<string> To,
    List<string>? Cc,
    string Subject,
    string? Body,
    DateTimeOffset SentAt,
    int AttachmentCount)
{
    public const int MaxBodyLength = 20_000;

    public ProviderMessage ToProviderMessage() => new(
        EntryId,
        string.IsNullOrWhiteSpace(MessageId) ? null : MessageId.Trim(),
        null,
        string.IsNullOrWhiteSpace(InReplyTo) ? null : InReplyTo.Trim(),
        (References ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries),
        From.Trim(),
        FromName,
        To.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList(),
        (Cc ?? new List<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList(),
        Subject ?? string.Empty,
        Body is { Length: > MaxBodyLength } ? Body[..MaxBodyLength] : Body,
        null,
        SentAt.ToUniversalTime(),
        Enumerable.Range(0, Math.Clamp(AttachmentCount, 0, 50)).Select(i => new ProviderAttachmentSummary($"attachment-{i + 1}", null, 0)).ToList());
}

public record AgentSentMessagesRequest(List<AgentSentMessageDto> Messages);

public record AgentSentMessagesResult(int Received, int RepliesStored, int InternalStored);
