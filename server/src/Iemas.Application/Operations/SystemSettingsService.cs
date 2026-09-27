using System.Globalization;
using Iemas.Application.Common;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Common.Security;
using Iemas.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Operations;

public enum SettingValueType
{
    Text,
    Integer,
    TimeZone,
}

public record SettingDefinition(
    string Key,
    string Group,
    string Label,
    string Description,
    SettingValueType Type,
    string DefaultValue,
    int? Min = null,
    int? Max = null);

public record SystemSettingDto(
    string Key,
    string Group,
    string Label,
    string Description,
    string Type,
    string Value,
    string DefaultValue,
    bool IsOverridden,
    int? Min,
    int? Max,
    string? UpdatedByEmail,
    DateTimeOffset? UpdatedAt);

public record UpdateSystemSettingsRequest(Dictionary<string, string> Values);

public record PublicSystemSettingsDto(string OrganizationName);

public static class SystemSettingKeys
{
    public const string OrganizationName = "general.organization_name";
    public const string DefaultTimeZone = "general.default_time_zone";
    public const string PasswordMinLength = "security.password_min_length";
}

/// <summary>
/// A fixed catalog of system settings (no free-form keys), each with a validated type and a
/// default. Only overrides are stored. Retention values (§90) are recorded policy; no automatic
/// purge job acts on them yet.
/// </summary>
public class SystemSettingsService
{
    private const string RetentionDescription =
        "Days to keep; 0 keeps indefinitely. Recorded retention policy (§90) — no automatic purge job acts on it yet.";

    public static readonly IReadOnlyList<SettingDefinition> Catalog = new List<SettingDefinition>
    {
        new(SystemSettingKeys.OrganizationName, "General", "Organization name",
            "Shown in the CMS header.", SettingValueType.Text, "IEMAS", 1, 100),
        new(SystemSettingKeys.DefaultTimeZone, "General", "Default time zone",
            "IANA time zone (e.g. Asia/Manila) used to display dates in notification messages.", SettingValueType.TimeZone, "UTC"),
        new(SystemSettingKeys.PasswordMinLength, "Security", "Minimum password length",
            "Enforced when creating users and changing or resetting passwords.", SettingValueType.Integer, "10", 8, 128),
        new("retention.raw_email_content", "Retention", "Raw email content", RetentionDescription, SettingValueType.Integer, "0", 0, 36500),
        new("retention.email_metadata", "Retention", "Email metadata", RetentionDescription, SettingValueType.Integer, "0", 0, 36500),
        new("retention.cases", "Retention", "Cases", RetentionDescription, SettingValueType.Integer, "0", 0, 36500),
        new("retention.case_history", "Retention", "Case History", RetentionDescription, SettingValueType.Integer, "0", 0, 36500),
        new("retention.ai_results", "Retention", "AI results", RetentionDescription, SettingValueType.Integer, "0", 0, 36500),
        new("retention.employee_activity", "Retention", "Employee Activity", RetentionDescription, SettingValueType.Integer, "0", 0, 36500),
        new("retention.audit_logs", "Retention", "Audit Logs", RetentionDescription, SettingValueType.Integer, "0", 0, 36500),
        new("retention.agent_logs", "Retention", "Agent technical logs", RetentionDescription, SettingValueType.Integer, "0", 0, 36500),
        new("retention.outbound_email_logs", "Retention", "Outbound email logs", RetentionDescription, SettingValueType.Integer, "0", 0, 36500),
    };

    private readonly IAppDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditService _auditService;

    public SystemSettingsService(IAppDbContext db, ICurrentUserService currentUser, IAuditService auditService)
    {
        _db = db;
        _currentUser = currentUser;
        _auditService = auditService;
    }

    public static async Task<string> GetValueAsync(IAppDbContext db, string key, CancellationToken cancellationToken)
    {
        var definition = Catalog.First(d => d.Key == key);
        var stored = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == key)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);
        return stored ?? definition.DefaultValue;
    }

    public static async Task<int> GetIntAsync(IAppDbContext db, string key, CancellationToken cancellationToken) =>
        int.Parse(await GetValueAsync(db, key, cancellationToken), CultureInfo.InvariantCulture);

    public static async Task<TimeZoneInfo> GetDefaultTimeZoneAsync(IAppDbContext db, CancellationToken cancellationToken)
    {
        var id = await GetValueAsync(db, SystemSettingKeys.DefaultTimeZone, cancellationToken);
        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Utc;
    }

    public async Task<List<SystemSettingDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        var stored = await _db.SystemSettings.AsNoTracking().ToListAsync(cancellationToken);
        return Catalog.Select(d =>
        {
            var row = stored.FirstOrDefault(s => s.Key == d.Key);
            return new SystemSettingDto(
                d.Key, d.Group, d.Label, d.Description, d.Type.ToString(),
                row?.Value ?? d.DefaultValue, d.DefaultValue, row is not null,
                d.Min, d.Max, row?.UpdatedByEmail, row?.UpdatedAt ?? row?.CreatedAt);
        }).ToList();
    }

    public async Task<PublicSystemSettingsDto> GetPublicAsync(CancellationToken cancellationToken) =>
        new(await GetValueAsync(_db, SystemSettingKeys.OrganizationName, cancellationToken));

    /// <summary>All-or-nothing: every submitted value is validated before any is written.</summary>
    public async Task<Result<List<SystemSettingDto>>> UpdateAsync(UpdateSystemSettingsRequest request, CancellationToken cancellationToken)
    {
        if (request.Values is null || request.Values.Count == 0)
        {
            return Result<List<SystemSettingDto>>.Failure("No settings were submitted.");
        }

        var normalized = new Dictionary<string, string>();
        foreach (var (key, rawValue) in request.Values)
        {
            var definition = Catalog.FirstOrDefault(d => d.Key == key);
            if (definition is null)
            {
                return Result<List<SystemSettingDto>>.Failure($"Unknown setting '{key}'.");
            }

            var (value, error) = Validate(definition, rawValue);
            if (error is not null)
            {
                return Result<List<SystemSettingDto>>.Failure(error);
            }
            normalized[key] = value!;
        }

        var keys = normalized.Keys.ToList();
        var existing = await _db.SystemSettings.Where(s => keys.Contains(s.Key)).ToListAsync(cancellationToken);
        var changes = new List<(string Key, string Detail)>();

        foreach (var (key, value) in normalized)
        {
            var row = existing.FirstOrDefault(s => s.Key == key);
            var oldValue = row?.Value ?? Catalog.First(d => d.Key == key).DefaultValue;
            if (row is null)
            {
                row = new SystemSetting { Key = key };
                _db.SystemSettings.Add(row);
            }
            else if (row.Value == value)
            {
                continue;
            }

            row.Value = value;
            row.UpdatedByUserId = _currentUser.UserId;
            row.UpdatedByEmail = _currentUser.Email;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            if (oldValue != value) changes.Add((key, $"{key}: '{oldValue}' -> '{value}'"));
        }

        await _db.SaveChangesAsync(cancellationToken);

        foreach (var (key, detail) in changes)
        {
            await _auditService.LogAsync("SYSTEM_SETTING_UPDATED", "SystemSetting", key, detail, cancellationToken);
        }

        return Result<List<SystemSettingDto>>.Success(await GetAllAsync(cancellationToken));
    }

    public async Task<Result<bool>> ResetAsync(string key, CancellationToken cancellationToken)
    {
        var definition = Catalog.FirstOrDefault(d => d.Key == key);
        if (definition is null)
        {
            return Result<bool>.Failure($"Unknown setting '{key}'.");
        }

        var row = await _db.SystemSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (row is null)
        {
            return Result<bool>.Success(true);
        }

        var oldValue = row.Value;
        _db.SystemSettings.Remove(row);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.LogAsync("SYSTEM_SETTING_RESET", "SystemSetting", key,
            $"{key}: '{oldValue}' -> default '{definition.DefaultValue}'", cancellationToken);
        return Result<bool>.Success(true);
    }

    private static (string? Value, string? Error) Validate(SettingDefinition definition, string? rawValue)
    {
        var value = rawValue?.Trim() ?? string.Empty;
        switch (definition.Type)
        {
            case SettingValueType.Integer:
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                {
                    return (null, $"{definition.Label} must be a whole number.");
                }
                if (number < definition.Min || number > definition.Max)
                {
                    return (null, $"{definition.Label} must be between {definition.Min} and {definition.Max}.");
                }
                return (number.ToString(CultureInfo.InvariantCulture), null);

            case SettingValueType.TimeZone:
                if (!TimeZoneInfo.TryFindSystemTimeZoneById(value, out _))
                {
                    return (null, $"'{value}' is not a recognized time zone.");
                }
                return (value, null);

            default:
                if (value.Length < (definition.Min ?? 0) || value.Length > (definition.Max ?? 1000))
                {
                    return (null, $"{definition.Label} must be between {definition.Min ?? 0} and {definition.Max ?? 1000} characters.");
                }
                if (InputSanitizer.ValidateFreeText(definition.Label, value) is { } error)
                {
                    return (null, error);
                }
                return (value, null);
        }
    }
}
