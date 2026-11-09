namespace Iemas.Application.Common.Interfaces;

public sealed record EmailRecipient(string Name, string Address);

public sealed record OutboundEmail(IReadOnlyList<EmailRecipient> To, string Subject, string TextBody);

public sealed record OutboundEmailResult(bool Sent, string? MessageId, string? Error);

/// <summary>
/// Module 11 / G10 - sends internal escalation email to management through the configured Outbound
/// email account. Never used for customer email: IEMAS still never sends, replies to or forwards
/// customer email.
/// </summary>
public interface IEscalationEmailSender
{
    Task<OutboundEmailResult> SendAsync(OutboundEmail email, CancellationToken cancellationToken);
}
