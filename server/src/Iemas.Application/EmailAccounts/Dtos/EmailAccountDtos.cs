using Iemas.Domain.Email;

namespace Iemas.Application.EmailAccounts.Dtos;

/// <summary>
/// Requirements §16 — API may return "hasCredential": true but never the credential itself.
/// This DTO has no password/secret field by construction, not by convention.
/// </summary>
public record EmailAccountDto(
    Guid Id,
    string EmailAddress,
    string? DisplayName,
    EmailAccountPurpose Purpose,
    EmailAccountKind Kind,
    EmailProtocol Protocol,
    string Host,
    int Port,
    string Encryption,
    string Username,
    EmailAuthMethod AuthMethod,
    Guid? OwnerEmployeeId,
    string? OwnerEmployeeName,
    string? ClassificationProfileName,
    bool MonitoringEnabled,
    bool IsActive,
    bool HasCredential,
    DateTimeOffset? LastTestedAt,
    bool? LastTestSucceeded,
    string? LastTestError,
    DateTimeOffset? ProcessEmailsReceivedAfter);

public record CreateEmailAccountRequest(
    string EmailAddress,
    string? DisplayName,
    EmailAccountPurpose Purpose,
    EmailAccountKind Kind,
    EmailProtocol Protocol,
    string Host,
    int Port,
    string Encryption,
    string Username,
    EmailAuthMethod AuthMethod,
    string Secret,
    Guid? OwnerEmployeeId,
    string? ClassificationProfileName,
    bool MonitoringEnabled,
    /// <summary>Null = from now on (mailbox history is kept but not turned into Cases).</summary>
    DateTimeOffset? ProcessEmailsReceivedAfter = null);

public record UpdateEmailAccountRequest(
    string? DisplayName,
    EmailAccountKind Kind,
    string Host,
    int Port,
    string Encryption,
    string Username,
    EmailAuthMethod AuthMethod,
    /// <summary>Null/empty = keep the existing stored credential unchanged (write-only after saving, §16).</summary>
    string? Secret,
    Guid? OwnerEmployeeId,
    string? ClassificationProfileName,
    bool MonitoringEnabled,
    bool IsActive,
    /// <summary>Null = no cut-off: every stored email is processed.</summary>
    DateTimeOffset? ProcessEmailsReceivedAfter = null);

public record TestConnectionResult(bool Succeeded, string? ErrorMessage, double DurationMs);
