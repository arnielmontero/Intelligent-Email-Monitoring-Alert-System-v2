using Iemas.Domain.Common;
using Iemas.Domain.Identity;

namespace Iemas.Domain.Email;

/// <summary>
/// Requirements §14 (monitored accounts), §18 (outbound config), §19 (inbound/outbound separation),
/// §93 (aliases/shared mailboxes). Covers both inbound monitored mailboxes and the outbound
/// notification account via <see cref="Purpose"/> — they share the same connection shape but
/// must never be conflated (an outbound account is never monitored as a customer mailbox).
/// </summary>
public class EmailAccount : Entity
{
    public string EmailAddress { get; set; } = string.Empty;
    public string? DisplayName { get; set; }

    public EmailAccountPurpose Purpose { get; set; }
    public EmailAccountKind Kind { get; set; } = EmailAccountKind.Primary;
    public EmailProtocol Protocol { get; set; }

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Encryption { get; set; } = "SSL/TLS";

    public string Username { get; set; } = string.Empty;
    public EmailAuthMethod AuthMethod { get; set; }

    /// <summary>Owning employee. Null for the outbound/system notification account (§18) which has no personal owner.</summary>
    public Guid? OwnerEmployeeId { get; set; }
    public Employee? OwnerEmployee { get; set; }

    /// <summary>
    /// Forward reference to the Classification Profile domain (§28), not built until Phase 4.
    /// Stored as a plain name for now; will be migrated to a proper FK once ClassificationProfile exists.
    /// Only meaningful for Inbound accounts.
    /// </summary>
    public string? ClassificationProfileName { get; set; }

    public bool MonitoringEnabled { get; set; }

    /// <summary>
    /// Only email received after this moment is classified and can create Cases. Older email is
    /// still stored (history, reply matching) but marked Historical. Null = process everything.
    /// </summary>
    public DateTimeOffset? ProcessEmailsReceivedAfter { get; set; }
    public bool IsActive { get; set; } = true;

    public EmailCredential? Credential { get; set; }

    public DateTimeOffset? LastTestedAt { get; set; }
    public bool? LastTestSucceeded { get; set; }
    public string? LastTestError { get; set; }
}
