namespace Iemas.Domain.Email;

/// <summary>Requirements §14.1 — provider abstraction; protocol is metadata the adapter dispatches on.</summary>
public enum EmailProtocol
{
    Imap = 0,
    MicrosoftGraph = 1,

    /// <summary>
    /// Requirements §18 — outbound (internal notification/escalation) email only. Send-only: this
    /// protocol never reads a mailbox, so FetchInboxMessagesAsync/FetchSentMessagesAsync on its
    /// IEmailProviderAdapter implementation are genuinely not applicable (not "not yet built") and
    /// throw NotSupportedException — an EmailAccount with this protocol should always also have
    /// Purpose=Outbound (enforced by EmailAccountService), which never calls those methods.
    /// </summary>
    Smtp = 2
}

/// <summary>Requirements §15 — email authentication data model.</summary>
public enum EmailAuthMethod
{
    Password = 0,
    OAuth2 = 1,
    AppPassword = 2
}

/// <summary>Requirements §19 — inbound monitored email vs. outbound internal notification email are separate systems.</summary>
public enum EmailAccountPurpose
{
    Inbound = 0,
    Outbound = 1
}

/// <summary>Requirements §93 — primary mailbox vs alias vs shared vs distribution address.</summary>
public enum EmailAccountKind
{
    Primary = 0,
    Alias = 1,
    Shared = 2,
    Distribution = 3
}
