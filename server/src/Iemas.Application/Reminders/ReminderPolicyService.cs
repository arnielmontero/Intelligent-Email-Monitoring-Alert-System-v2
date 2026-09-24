using Iemas.Application.Common;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Common.Security;
using Iemas.Application.Reminders.Dtos;
using Iemas.Domain.Ai;
using Iemas.Domain.Reminders;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Reminders;

/// <summary>Requirements §54 CMS — Reminder Policies list/create/update/delete, plus Reminder read/cancel/reschedule for administrative visibility. §67/§84 — every change is audited.</summary>
public class ReminderPolicyService
{
    private readonly IAppDbContext _db;
    private readonly IAuditService _auditService;

    public ReminderPolicyService(IAppDbContext db, IAuditService auditService)
    {
        _db = db;
        _auditService = auditService;
    }

    public async Task<List<ReminderPolicyDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        var policies = await _db.ReminderPolicies.Include(p => p.Holidays).OrderBy(p => p.Name).ToListAsync(cancellationToken);
        var profileNames = await _db.ClassificationProfiles.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
        return policies.Select(p => ToDto(p, profileNames)).ToList();
    }

    public async Task<ReminderPolicyDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var policy = await _db.ReminderPolicies.Include(p => p.Holidays).FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (policy is null) return null;
        var profileNames = await _db.ClassificationProfiles.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
        return ToDto(policy, profileNames);
    }

    public async Task<Result<ReminderPolicyDto>> CreateAsync(SaveReminderPolicyRequest request, CancellationToken cancellationToken)
    {
        var validation = Validate(request);
        if (validation is not null) return Result<ReminderPolicyDto>.Failure(validation);

        // Cheap pre-check ahead of the DB's own unique index (the authoritative guard for a
        // genuine concurrent race, same DbUpdateException-catch pattern used elsewhere) — this
        // check gives a clean error message immediately in the common non-racing case.
        if (await _db.ReminderPolicies.AnyAsync(p => p.Name == request.Name, cancellationToken))
        {
            return Result<ReminderPolicyDto>.Failure("A Reminder Policy with this name already exists.");
        }

        var policy = new ReminderPolicy
        {
            Name = request.Name,
            Description = request.Description,
            Enabled = request.Enabled,
            IsDefault = request.IsDefault,
            ClassificationProfileId = request.ClassificationProfileId,
            InitialDelay = request.InitialDelay,
            ReminderInterval = request.ReminderInterval,
            MaxReminders = request.MaxReminders,
            MinimumInterval = request.MinimumInterval,
            RestrictToBusinessHours = request.RestrictToBusinessHours,
            BusinessHoursStart = request.BusinessHoursStart,
            BusinessHoursEnd = request.BusinessHoursEnd,
            ExcludeWeekends = request.ExcludeWeekends,
            TimeZoneId = request.TimeZoneId,
            ExpirationWindow = request.ExpirationWindow,
            EscalationThresholdReminderCount = request.EscalationThresholdReminderCount,
        };

        if (request.IsDefault)
        {
            await ClearExistingDefaultAsync(cancellationToken);
        }

        foreach (var h in request.Holidays)
        {
            policy.Holidays.Add(new ReminderPolicyHoliday { Date = h.Date, Label = h.Label });
        }

        _db.ReminderPolicies.Add(policy);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Result<ReminderPolicyDto>.Failure("A Reminder Policy with this name already exists.");
        }

        await _auditService.LogAsync("ReminderPolicy.Created", "ReminderPolicy", policy.Id.ToString(), $"Created policy \"{policy.Name}\".", cancellationToken);

        var profileNames = await _db.ClassificationProfiles.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
        return Result<ReminderPolicyDto>.Success(ToDto(policy, profileNames));
    }

    public async Task<Result<ReminderPolicyDto>> UpdateAsync(Guid id, SaveReminderPolicyRequest request, CancellationToken cancellationToken)
    {
        var validation = Validate(request);
        if (validation is not null) return Result<ReminderPolicyDto>.Failure(validation);

        var policy = await _db.ReminderPolicies.Include(p => p.Holidays).FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (policy is null) return Result<ReminderPolicyDto>.Failure("Reminder Policy not found.");

        if (await _db.ReminderPolicies.AnyAsync(p => p.Id != id && p.Name == request.Name, cancellationToken))
        {
            return Result<ReminderPolicyDto>.Failure("A Reminder Policy with this name already exists.");
        }

        if (request.IsDefault && !policy.IsDefault)
        {
            await ClearExistingDefaultAsync(cancellationToken);
        }

        policy.Name = request.Name;
        policy.Description = request.Description;
        policy.Enabled = request.Enabled;
        policy.IsDefault = request.IsDefault;
        policy.ClassificationProfileId = request.ClassificationProfileId;
        policy.InitialDelay = request.InitialDelay;
        policy.ReminderInterval = request.ReminderInterval;
        policy.MaxReminders = request.MaxReminders;
        policy.MinimumInterval = request.MinimumInterval;
        policy.RestrictToBusinessHours = request.RestrictToBusinessHours;
        policy.BusinessHoursStart = request.BusinessHoursStart;
        policy.BusinessHoursEnd = request.BusinessHoursEnd;
        policy.ExcludeWeekends = request.ExcludeWeekends;
        policy.TimeZoneId = request.TimeZoneId;
        policy.ExpirationWindow = request.ExpirationWindow;
        policy.EscalationThresholdReminderCount = request.EscalationThresholdReminderCount;
        policy.UpdatedAt = DateTimeOffset.UtcNow;

        policy.Holidays.Clear();
        foreach (var h in request.Holidays)
        {
            policy.Holidays.Add(new ReminderPolicyHoliday { Date = h.Date, Label = h.Label });
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Result<ReminderPolicyDto>.Failure("A Reminder Policy with this name already exists.");
        }

        await _auditService.LogAsync("ReminderPolicy.Updated", "ReminderPolicy", policy.Id.ToString(), $"Updated policy \"{policy.Name}\".", cancellationToken);

        var profileNames = await _db.ClassificationProfiles.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
        return Result<ReminderPolicyDto>.Success(ToDto(policy, profileNames));
    }

    public async Task<Result<bool>> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var policy = await _db.ReminderPolicies.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (policy is null) return Result<bool>.Failure("Reminder Policy not found.");

        var inUse = await _db.Reminders.AnyAsync(r => r.ReminderPolicyId == id && r.Status == Domain.Reminders.ReminderStatus.Scheduled, cancellationToken);
        if (inUse)
        {
            return Result<bool>.Failure("This policy has Scheduled reminders pending; disable it instead of deleting, or wait for them to resolve.");
        }

        _db.ReminderPolicies.Remove(policy);
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("ReminderPolicy.Deleted", "ReminderPolicy", id.ToString(), $"Deleted policy \"{policy.Name}\".", cancellationToken);
        return Result<bool>.Success(true);
    }

    private async Task ClearExistingDefaultAsync(CancellationToken cancellationToken)
    {
        var currentDefaults = await _db.ReminderPolicies.Where(p => p.IsDefault).ToListAsync(cancellationToken);
        foreach (var d in currentDefaults)
        {
            d.IsDefault = false;
        }
    }

    private static string? Validate(SaveReminderPolicyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return "Name is required.";
        if (InputSanitizer.ValidateFreeText("Name", request.Name) is { } nameError) return nameError;
        if (request.Description is not null && InputSanitizer.ValidateFreeText("Description", request.Description) is { } descriptionError) return descriptionError;
        if (request.MaxReminders < 1) return "Maximum reminders must be at least 1 (§54: no infinite reminder loops).";
        if (request.InitialDelay < TimeSpan.Zero) return "Initial delay cannot be negative.";
        if (request.ReminderInterval <= TimeSpan.Zero) return "Reminder interval must be positive.";
        if (request.MinimumInterval < TimeSpan.Zero) return "Minimum interval cannot be negative.";
        if (request.RestrictToBusinessHours && request.BusinessHoursStart >= request.BusinessHoursEnd)
        {
            return "Business hours start must be before business hours end.";
        }
        if (ReminderTimingCalculator.ResolveTimeZone(request.TimeZoneId).Id != request.TimeZoneId && !string.Equals(request.TimeZoneId, "UTC", StringComparison.OrdinalIgnoreCase))
        {
            return "Unrecognized time zone id.";
        }
        return null;
    }

    private static ReminderPolicyDto ToDto(ReminderPolicy p, Dictionary<Guid, string> profileNames) => new(
        p.Id, p.Name, p.Description, p.Enabled, p.IsDefault,
        p.ClassificationProfileId, p.ClassificationProfileId is Guid pid && profileNames.TryGetValue(pid, out var name) ? name : null,
        p.InitialDelay, p.ReminderInterval, p.MaxReminders, p.MinimumInterval,
        p.RestrictToBusinessHours, p.BusinessHoursStart, p.BusinessHoursEnd, p.ExcludeWeekends,
        p.TimeZoneId, p.ExpirationWindow, p.EscalationThresholdReminderCount,
        p.Holidays.OrderBy(h => h.Date).Select(h => new ReminderPolicyHolidayDto(h.Id, h.Date, h.Label)).ToList());
}
