using Iemas.Application.Common;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Common.Providers;
using Iemas.Application.Common.Security;
using Iemas.Application.EmailAccounts.Dtos;
using Iemas.Domain.Email;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.EmailAccounts;

/// <summary>
/// Requirements §14, §16, §18, §19 — email account management with credential security.
/// The plaintext secret exists only transiently inside request handling (bound from the
/// request DTO, encrypted immediately, then discarded); it is never assigned to any field
/// that gets logged, cached, or returned.
/// </summary>
public class EmailAccountService
{
    private readonly IAppDbContext _db;
    private readonly ICredentialEncryptionService _encryptionService;
    private readonly IEmailProviderAdapterResolver _adapterResolver;
    private readonly IAuditService _auditService;

    public EmailAccountService(
        IAppDbContext db,
        ICredentialEncryptionService encryptionService,
        IEmailProviderAdapterResolver adapterResolver,
        IAuditService auditService)
    {
        _db = db;
        _encryptionService = encryptionService;
        _adapterResolver = adapterResolver;
        _auditService = auditService;
    }

    public async Task<List<EmailAccountDto>> GetAllAsync(EmailAccountPurpose? purpose, CancellationToken cancellationToken)
    {
        var query = _db.EmailAccounts.AsNoTracking();
        if (purpose is not null)
        {
            query = query.Where(a => a.Purpose == purpose);
        }

        return await ProjectAndOrder(query).ToListAsync(cancellationToken);
    }

    public async Task<EmailAccountDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        // Filter on the entity query before projecting into the DTO record — EF Core fails to
        // translate a predicate applied after a record-constructing Select, the same issue as
        // ORDER BY after Select (see ProjectAndOrder below; found via live Docker verification).
        var query = _db.EmailAccounts.AsNoTracking().Where(a => a.Id == id);
        return await ProjectAndOrder(query).FirstOrDefaultAsync(cancellationToken);
    }

    private IQueryable<EmailAccountDto> ProjectedQuery() => ProjectAndOrder(_db.EmailAccounts.AsNoTracking());

    private static IQueryable<EmailAccountDto> ProjectAndOrder(IQueryable<EmailAccount> source)
    {
        // Order before projecting into the DTO record — EF Core fails to translate ORDER BY
        // applied after a record-constructing Select (discovered via live Docker verification;
        // see Phase 2 bugs log in the progress tracker).
        // Credential is deliberately excluded from this projection entirely — not masked, not
        // included-then-stripped. The query never touches EncryptedSecret/Nonce/Tag (§16).
        return source
            .OrderBy(a => a.EmailAddress)
            .Select(a => new EmailAccountDto(
                a.Id,
                a.EmailAddress,
                a.DisplayName,
                a.Purpose,
                a.Kind,
                a.Protocol,
                a.Host,
                a.Port,
                a.Encryption,
                a.Username,
                a.AuthMethod,
                a.OwnerEmployeeId,
                a.OwnerEmployee != null ? a.OwnerEmployee.FullName : null,
                a.ClassificationProfileName,
                a.MonitoringEnabled,
                a.IsActive,
                a.Credential != null,
                a.LastTestedAt,
                a.LastTestSucceeded,
                a.LastTestError,
                a.ProcessEmailsReceivedAfter));
    }

    public async Task<Result<EmailAccountDto>> CreateAsync(CreateEmailAccountRequest request, CancellationToken cancellationToken)
    {
        var emailAddress = request.EmailAddress.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(emailAddress) || !emailAddress.Contains('@'))
        {
            return Result<EmailAccountDto>.Failure("A valid email address is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Secret))
        {
            return Result<EmailAccountDto>.Failure("A credential secret is required when creating an account.");
        }

        if (request.Port is <= 0 or > 65535)
        {
            return Result<EmailAccountDto>.Failure("Port must be between 1 and 65535.");
        }

        if (await _db.EmailAccounts.AnyAsync(a => a.EmailAddress == emailAddress && a.Purpose == request.Purpose, cancellationToken))
        {
            return Result<EmailAccountDto>.Failure("An email account with this address and purpose already exists.");
        }

        if (await ReservedAddresses.IsSuperAdministratorLoginAsync(_db, emailAddress, cancellationToken))
        {
            return Result<EmailAccountDto>.Failure(ReservedAddresses.SuperAdministratorMessage);
        }

        if (request.OwnerEmployeeId is not null
            && !await _db.Employees.AnyAsync(e => e.Id == request.OwnerEmployeeId, cancellationToken))
        {
            return Result<EmailAccountDto>.Failure("Owner employee not found.");
        }

        // Requirements §19 — the outbound notification account must not be monitored as a
        // customer mailbox; monitoring only ever applies to Inbound accounts.
        if (request.Purpose == EmailAccountPurpose.Outbound && request.MonitoringEnabled)
        {
            return Result<EmailAccountDto>.Failure("Monitoring cannot be enabled on an outbound account.");
        }

        var displayName = request.DisplayName?.Trim();
        if (displayName is not null && InputSanitizer.ValidateFreeText("Display name", displayName) is { } displayNameError)
        {
            return Result<EmailAccountDto>.Failure(displayNameError);
        }

        var account = new EmailAccount
        {
            EmailAddress = emailAddress,
            DisplayName = displayName,
            Purpose = request.Purpose,
            Kind = request.Kind,
            Protocol = request.Protocol,
            Host = request.Host.Trim(),
            Port = request.Port,
            Encryption = request.Encryption.Trim(),
            Username = request.Username.Trim(),
            AuthMethod = request.AuthMethod,
            OwnerEmployeeId = request.OwnerEmployeeId,
            ClassificationProfileName = request.Purpose == EmailAccountPurpose.Inbound ? request.ClassificationProfileName?.Trim() : null,
            MonitoringEnabled = request.MonitoringEnabled,
            ProcessEmailsReceivedAfter = request.Purpose == EmailAccountPurpose.Inbound
                ? request.ProcessEmailsReceivedAfter ?? DateTimeOffset.UtcNow
                : null,
        };

        var encrypted = _encryptionService.Encrypt(request.Secret);
        account.Credential = new EmailCredential
        {
            EncryptedSecret = encrypted.Ciphertext,
            Nonce = encrypted.Nonce,
            Tag = encrypted.Tag,
            KeyId = encrypted.KeyId
        };

        _db.EmailAccounts.Add(account);
        await _db.SaveChangesAsync(cancellationToken);

        // Audit records the action and account identity, never the secret (§16 — never in logs).
        await _auditService.LogAsync("EMAIL_ACCOUNT_CREATED", "EmailAccount", account.Id.ToString(), $"{account.EmailAddress} ({account.Purpose})", cancellationToken);

        return Result<EmailAccountDto>.Success(await GetByIdAsync(account.Id, cancellationToken) ?? throw new InvalidOperationException());
    }

    public async Task<Result<EmailAccountDto>> UpdateAsync(Guid id, UpdateEmailAccountRequest request, CancellationToken cancellationToken)
    {
        var account = await _db.EmailAccounts
            .Include(a => a.Credential)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

        if (account is null)
        {
            return Result<EmailAccountDto>.Failure("Email account not found.");
        }

        if (request.Port is <= 0 or > 65535)
        {
            return Result<EmailAccountDto>.Failure("Port must be between 1 and 65535.");
        }

        if (request.OwnerEmployeeId is not null
            && !await _db.Employees.AnyAsync(e => e.Id == request.OwnerEmployeeId, cancellationToken))
        {
            return Result<EmailAccountDto>.Failure("Owner employee not found.");
        }

        if (account.Purpose == EmailAccountPurpose.Outbound && request.MonitoringEnabled)
        {
            return Result<EmailAccountDto>.Failure("Monitoring cannot be enabled on an outbound account.");
        }

        var displayName = request.DisplayName?.Trim();
        if (displayName is not null && InputSanitizer.ValidateFreeText("Display name", displayName) is { } displayNameError)
        {
            return Result<EmailAccountDto>.Failure(displayNameError);
        }

        account.DisplayName = displayName;
        account.Kind = request.Kind;
        account.Host = request.Host.Trim();
        account.Port = request.Port;
        account.Encryption = request.Encryption.Trim();
        account.Username = request.Username.Trim();
        account.AuthMethod = request.AuthMethod;
        account.OwnerEmployeeId = request.OwnerEmployeeId;
        account.ClassificationProfileName = account.Purpose == EmailAccountPurpose.Inbound ? request.ClassificationProfileName?.Trim() : null;
        account.MonitoringEnabled = request.MonitoringEnabled;
        account.IsActive = request.IsActive;
        account.UpdatedAt = DateTimeOffset.UtcNow;

        if (account.Purpose == EmailAccountPurpose.Inbound && account.ProcessEmailsReceivedAfter != request.ProcessEmailsReceivedAfter)
        {
            await ApplyCutoffAsync(account, request.ProcessEmailsReceivedAfter, cancellationToken);
        }

        // Requirements §16 — credential is write-only after saving. Only touched if a new
        // secret was explicitly supplied; otherwise the stored ciphertext is left untouched.
        if (!string.IsNullOrEmpty(request.Secret))
        {
            var encrypted = _encryptionService.Encrypt(request.Secret);

            if (account.Credential is null)
            {
                account.Credential = new EmailCredential { EmailAccountId = account.Id };
                _db.EmailCredentials.Add(account.Credential);
            }

            account.Credential.EncryptedSecret = encrypted.Ciphertext;
            account.Credential.Nonce = encrypted.Nonce;
            account.Credential.Tag = encrypted.Tag;
            account.Credential.KeyId = encrypted.KeyId;
            account.Credential.RotatedAt = DateTimeOffset.UtcNow;

            await _auditService.LogAsync("EMAIL_ACCOUNT_CREDENTIAL_ROTATED", "EmailAccount", account.Id.ToString(), account.EmailAddress, cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.LogAsync("EMAIL_ACCOUNT_UPDATED", "EmailAccount", account.Id.ToString(), account.EmailAddress, cancellationToken);

        return Result<EmailAccountDto>.Success(await GetByIdAsync(account.Id, cancellationToken) ?? throw new InvalidOperationException());
    }

    public async Task<Result<bool>> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken)
    {
        var account = await _db.EmailAccounts.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (account is null)
        {
            return Result<bool>.Failure("Email account not found.");
        }

        account.IsActive = isActive;
        if (!isActive)
        {
            // Requirements §91 — deactivating an account must stop monitoring too; it must not
            // silently keep polling a mailbox the admin just disabled.
            account.MonitoringEnabled = false;
        }
        account.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.LogAsync(isActive ? "EMAIL_ACCOUNT_ACTIVATED" : "EMAIL_ACCOUNT_DEACTIVATED", "EmailAccount", id.ToString(), account.EmailAddress, cancellationToken);

        return Result<bool>.Success(true);
    }

    /// <summary>
    /// Moves stored email across the new cut-off: waiting email now before it becomes Historical,
    /// and Historical email now after it goes back into classification. Already-processed email
    /// and existing Cases are never touched.
    /// </summary>
    private async Task ApplyCutoffAsync(EmailAccount account, DateTimeOffset? cutoff, CancellationToken cancellationToken)
    {
        var previous = account.ProcessEmailsReceivedAfter;
        account.ProcessEmailsReceivedAfter = cutoff;

        var toHistorical = cutoff is DateTimeOffset newCutoff
            ? await _db.EmailMessages
                .Where(m => m.EmailAccountId == account.Id && m.ProcessingStatus == EmailProcessingStatus.PendingClassification && m.ReceivedAt < newCutoff)
                .ToListAsync(cancellationToken)
            : new List<EmailMessage>();
        foreach (var message in toHistorical) message.ProcessingStatus = EmailProcessingStatus.Historical;

        var backToPending = await _db.EmailMessages
            .Where(m => m.EmailAccountId == account.Id && m.ProcessingStatus == EmailProcessingStatus.Historical
                        && (cutoff == null || m.ReceivedAt >= cutoff))
            .ToListAsync(cancellationToken);
        foreach (var message in backToPending) message.ProcessingStatus = EmailProcessingStatus.PendingClassification;

        await _auditService.LogAsync("EMAIL_ACCOUNT_CUTOFF_CHANGED", "EmailAccount", account.Id.ToString(),
            $"{account.EmailAddress}: process email received after {(previous?.ToString("u") ?? "(everything)")} -> {(cutoff?.ToString("u") ?? "(everything)")}; "
            + $"{toHistorical.Count} waiting email(s) set Historical, {backToPending.Count} Historical email(s) queued for classification",
            cancellationToken);
    }

    /// <summary>
    /// Permanently removes a deactivated account with its encrypted credential, sync position and
    /// intake log. An account with stored email or Cases is kept: that email is the evidence
    /// behind Case history (§66, §90).
    /// </summary>
    public async Task<Result<bool>> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var account = await _db.EmailAccounts.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (account is null)
        {
            return Result<bool>.Failure("Email account not found.");
        }
        if (account.IsActive)
        {
            return Result<bool>.Failure("Deactivate this account before deleting it.");
        }

        var messageCount = await _db.EmailMessages.CountAsync(m => m.EmailAccountId == id, cancellationToken);
        var caseCount = await _db.Cases.CountAsync(c => c.EmailAccountId == id, cancellationToken);
        if (messageCount > 0 || caseCount > 0)
        {
            return Result<bool>.Failure(
                $"This account is kept because it has {messageCount} stored email(s) and {caseCount} Case(s) that depend on it. It stays deactivated and is not polled.");
        }

        var intakeLogs = await _db.EmailIntakeLogs.Where(l => l.EmailAccountId == id).ToListAsync(cancellationToken);
        _db.EmailIntakeLogs.RemoveRange(intakeLogs);
        _db.EmailSyncStates.RemoveRange(await _db.EmailSyncStates.Where(s => s.EmailAccountId == id).ToListAsync(cancellationToken));
        _db.EmailCredentials.RemoveRange(await _db.EmailCredentials.Where(c => c.EmailAccountId == id).ToListAsync(cancellationToken));
        _db.EmailAccounts.Remove(account);
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("EMAIL_ACCOUNT_DELETED", "EmailAccount", id.ToString(),
            $"{account.EmailAddress} ({account.Purpose}); credential and {intakeLogs.Count} intake log line(s) removed", cancellationToken);
        return Result<bool>.Success(true);
    }

    /// <summary>
    /// Requirements §14, §18 — Test Connection. Decrypts the stored credential only in memory,
    /// for the duration of the provider call, and never returns it to the caller.
    /// </summary>
    public async Task<Result<TestConnectionResult>> TestConnectionAsync(Guid id, CancellationToken cancellationToken)
    {
        var account = await _db.EmailAccounts
            .Include(a => a.Credential)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

        if (account is null)
        {
            return Result<TestConnectionResult>.Failure("Email account not found.");
        }

        if (account.Credential is null)
        {
            return Result<TestConnectionResult>.Failure("No credential is configured for this account.");
        }

        string secret;
        try
        {
            secret = _encryptionService.Decrypt(new(
                account.Credential.EncryptedSecret,
                account.Credential.Nonce,
                account.Credential.Tag,
                account.Credential.KeyId));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // AES-GCM authentication tag mismatch — corrupted/tampered ciphertext, or the key was
            // rotated without re-encrypting this row. Must fail cleanly, not crash the request or
            // fall back to treating the corrupted bytes as a usable secret.
            account.LastTestedAt = DateTimeOffset.UtcNow;
            account.LastTestSucceeded = false;
            account.LastTestError = "Stored credential could not be decrypted. It may be corrupted or encrypted with a rotated key; please re-enter the credential.";
            await _db.SaveChangesAsync(cancellationToken);

            await _auditService.LogAsync("EMAIL_ACCOUNT_CREDENTIAL_DECRYPT_FAILED", "EmailAccount", account.Id.ToString(), account.EmailAddress, cancellationToken);

            return Result<TestConnectionResult>.Success(new TestConnectionResult(false, account.LastTestError, 0));
        }

        var settings = new EmailProviderConnectionSettings(
            account.Protocol,
            account.Host,
            account.Port,
            account.Encryption,
            account.Username,
            account.AuthMethod,
            secret);

        var adapter = _adapterResolver.Resolve(account.Protocol);
        var testResult = await adapter.TestConnectionAsync(settings, cancellationToken);

        account.LastTestedAt = DateTimeOffset.UtcNow;
        account.LastTestSucceeded = testResult.Succeeded;
        account.LastTestError = testResult.Succeeded ? null : testResult.ErrorMessage;
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(
            testResult.Succeeded ? "EMAIL_ACCOUNT_TEST_SUCCEEDED" : "EMAIL_ACCOUNT_TEST_FAILED",
            "EmailAccount",
            account.Id.ToString(),
            account.EmailAddress,
            cancellationToken);

        return Result<TestConnectionResult>.Success(new TestConnectionResult(
            testResult.Succeeded,
            testResult.ErrorMessage,
            testResult.Duration.TotalMilliseconds));
    }
}
