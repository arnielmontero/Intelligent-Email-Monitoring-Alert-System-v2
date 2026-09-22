using Iemas.Application.Common;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Escalations.Dtos;
using Iemas.Domain.Escalations;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Escalations;

/// <summary>Requirements §57 CMS — Escalation Policies CRUD (including nested Levels), mirroring ReminderPolicyService's shape.</summary>
public class EscalationPolicyService
{
    private readonly IAppDbContext _db;
    private readonly IAuditService _auditService;
    private readonly EscalationService _escalationService;

    public EscalationPolicyService(IAppDbContext db, IAuditService auditService, EscalationService escalationService)
    {
        _db = db;
        _auditService = auditService;
        _escalationService = escalationService;
    }

    public async Task<List<EscalationPolicyDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        var policies = await _db.EscalationPolicies.Include(p => p.Levels).OrderBy(p => p.Name).ToListAsync(cancellationToken);
        return await ToDtosAsync(policies, cancellationToken);
    }

    public async Task<EscalationPolicyDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var policy = await _db.EscalationPolicies.Include(p => p.Levels).FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (policy is null) return null;
        return (await ToDtosAsync(new List<EscalationPolicy> { policy }, cancellationToken))[0];
    }

    public async Task<Result<EscalationPolicyDto>> CreateAsync(SaveEscalationPolicyRequest request, CancellationToken cancellationToken)
    {
        var validation = await ValidateAsync(request, cancellationToken);
        if (validation is not null) return Result<EscalationPolicyDto>.Failure(validation);

        if (await _db.EscalationPolicies.AnyAsync(p => p.Name == request.Name, cancellationToken))
        {
            return Result<EscalationPolicyDto>.Failure("An Escalation Policy with this name already exists.");
        }

        var policy = new EscalationPolicy
        {
            Name = request.Name,
            Description = request.Description,
            Enabled = request.Enabled,
            IsDefault = request.IsDefault,
            ClassificationProfileId = request.ClassificationProfileId,
            Categories = request.Categories,
            Priority = request.Priority,
            TriggerReminderCount = request.TriggerReminderCount,
            GracePeriod = request.GracePeriod,
            Cooldown = request.Cooldown,
            MaximumLevel = request.MaximumLevel,
            Channel = request.Channel,
        };

        if (request.IsDefault)
        {
            await ClearExistingDefaultAsync(cancellationToken);
        }

        foreach (var l in request.Levels)
        {
            policy.Levels.Add(new EscalationLevel
            {
                Level = l.Level,
                DelayAfterPreviousLevel = l.DelayAfterPreviousLevel,
                RecipientType = l.RecipientType,
                SpecificEmployeeId = l.SpecificEmployeeId,
                SpecificGroupId = l.SpecificGroupId,
            });
        }

        _db.EscalationPolicies.Add(policy);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Result<EscalationPolicyDto>.Failure("An Escalation Policy with this name already exists, or a Level number is duplicated.");
        }

        await _auditService.LogAsync("EscalationPolicy.Created", "EscalationPolicy", policy.Id.ToString(), $"Created policy \"{policy.Name}\".", cancellationToken);

        return Result<EscalationPolicyDto>.Success((await ToDtosAsync(new List<EscalationPolicy> { policy }, cancellationToken))[0]);
    }

    public async Task<Result<EscalationPolicyDto>> UpdateAsync(Guid id, SaveEscalationPolicyRequest request, CancellationToken cancellationToken)
    {
        var validation = await ValidateAsync(request, cancellationToken);
        if (validation is not null) return Result<EscalationPolicyDto>.Failure(validation);

        var policy = await _db.EscalationPolicies.Include(p => p.Levels).FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (policy is null) return Result<EscalationPolicyDto>.Failure("Escalation Policy not found.");

        if (await _db.EscalationPolicies.AnyAsync(p => p.Id != id && p.Name == request.Name, cancellationToken))
        {
            return Result<EscalationPolicyDto>.Failure("An Escalation Policy with this name already exists.");
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
        policy.Categories = request.Categories;
        policy.Priority = request.Priority;
        policy.TriggerReminderCount = request.TriggerReminderCount;
        policy.GracePeriod = request.GracePeriod;
        policy.Cooldown = request.Cooldown;
        policy.MaximumLevel = request.MaximumLevel;
        policy.Channel = request.Channel;
        policy.UpdatedAt = DateTimeOffset.UtcNow;

        // Explicit RemoveRange rather than policy.Levels.Clear() — on an already-tracked,
        // pre-existing parent, EF Core's change tracker does not reliably translate a
        // navigation-collection Clear() into deletions of the removed children (the same class of
        // "pre-existing parent vs. newly-tracked parent" change-tracking issue Phase 7 hit with
        // AgentCredential — see tracker Bug #11). Explicit Add/Remove on the DbSet is the
        // established, verified-safe pattern for this exact shape.
        var existingLevels = await _db.EscalationLevels.Where(l => l.EscalationPolicyId == policy.Id).ToListAsync(cancellationToken);
        _db.EscalationLevels.RemoveRange(existingLevels);
        policy.Levels.Clear();

        foreach (var l in request.Levels)
        {
            _db.EscalationLevels.Add(new EscalationLevel
            {
                EscalationPolicyId = policy.Id,
                Level = l.Level,
                DelayAfterPreviousLevel = l.DelayAfterPreviousLevel,
                RecipientType = l.RecipientType,
                SpecificEmployeeId = l.SpecificEmployeeId,
                SpecificGroupId = l.SpecificGroupId,
            });
        }

        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("EscalationPolicy.Updated", "EscalationPolicy", policy.Id.ToString(), $"Updated policy \"{policy.Name}\".", cancellationToken);

        var reloaded = await _db.EscalationPolicies.Include(p => p.Levels).AsNoTracking().FirstAsync(p => p.Id == policy.Id, cancellationToken);
        return Result<EscalationPolicyDto>.Success((await ToDtosAsync(new List<EscalationPolicy> { reloaded }, cancellationToken))[0]);
    }

    public async Task<Result<bool>> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var policy = await _db.EscalationPolicies.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (policy is null) return Result<bool>.Failure("Escalation Policy not found.");

        _db.EscalationPolicies.Remove(policy);
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("EscalationPolicy.Deleted", "EscalationPolicy", id.ToString(), $"Deleted policy \"{policy.Name}\".", cancellationToken);
        return Result<bool>.Success(true);
    }

    /// <summary>§57 "Test Policy" — dry-run against a specific Case, no state written.</summary>
    public async Task<Result<TestEscalationPolicyResult>> TestAsync(Guid policyId, Guid caseId, CancellationToken cancellationToken)
    {
        var result = await _escalationService.TestPolicyAsync(policyId, caseId, cancellationToken);
        return Result<TestEscalationPolicyResult>.Success(result);
    }

    private async Task ClearExistingDefaultAsync(CancellationToken cancellationToken)
    {
        var currentDefaults = await _db.EscalationPolicies.Where(p => p.IsDefault).ToListAsync(cancellationToken);
        foreach (var d in currentDefaults)
        {
            d.IsDefault = false;
        }
    }

    private async Task<string?> ValidateAsync(SaveEscalationPolicyRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return "Name is required.";
        if (request.MaximumLevel < 1 || request.MaximumLevel > 3) return "Maximum Level must be between 1 and 3 (§58: V1 supports at most three levels).";
        if (request.TriggerReminderCount < 1) return "Trigger reminder count must be at least 1.";
        if (request.GracePeriod < TimeSpan.Zero) return "Grace period cannot be negative.";
        if (request.Cooldown < TimeSpan.Zero) return "Cooldown cannot be negative.";
        if (string.IsNullOrWhiteSpace(request.Channel)) return "Channel is required.";

        var levelNumbers = request.Levels.Select(l => l.Level).ToList();
        if (levelNumbers.Count != levelNumbers.Distinct().Count()) return "Level numbers must be unique within a policy.";
        if (levelNumbers.Any(n => n < 1 || n > request.MaximumLevel)) return "Every Level's number must be between 1 and Maximum Level.";

        foreach (var level in request.Levels)
        {
            if (level.RecipientType == Domain.Escalations.EscalationRecipientType.SpecificEmployee && level.SpecificEmployeeId is null)
            {
                return $"Level {level.Level}: Specific Employee recipient type requires SpecificEmployeeId.";
            }
            if (level.RecipientType == Domain.Escalations.EscalationRecipientType.SpecificGroup && level.SpecificGroupId is null)
            {
                return $"Level {level.Level}: Specific Group recipient type requires SpecificGroupId.";
            }
            if (level.SpecificEmployeeId is Guid empId && !await _db.Employees.AnyAsync(e => e.Id == empId, cancellationToken))
            {
                return $"Level {level.Level}: Specific Employee not found.";
            }
            if (level.SpecificGroupId is Guid groupId && !await _db.EscalationGroups.AnyAsync(g => g.Id == groupId, cancellationToken))
            {
                return $"Level {level.Level}: Specific Group not found.";
            }
        }

        return null;
    }

    private async Task<List<EscalationPolicyDto>> ToDtosAsync(List<EscalationPolicy> policies, CancellationToken cancellationToken)
    {
        var profileNames = await _db.ClassificationProfiles.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
        var employeeNames = await _db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.FullName, cancellationToken);
        var groupNames = await _db.EscalationGroups.AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken);

        return policies.Select(p => new EscalationPolicyDto(
            p.Id, p.Name, p.Description, p.Enabled, p.IsDefault,
            p.ClassificationProfileId, p.ClassificationProfileId is Guid pid && profileNames.TryGetValue(pid, out var pname) ? pname : null,
            p.Categories, p.Priority, p.TriggerReminderCount, p.GracePeriod, p.Cooldown, p.MaximumLevel, p.Channel,
            p.Levels.OrderBy(l => l.Level).Select(l => new EscalationLevelDto(
                l.Id, l.Level, l.DelayAfterPreviousLevel, l.RecipientType,
                l.SpecificEmployeeId, l.SpecificEmployeeId is Guid eid && employeeNames.TryGetValue(eid, out var ename) ? ename : null,
                l.SpecificGroupId, l.SpecificGroupId is Guid gid && groupNames.TryGetValue(gid, out var gname) ? gname : null)).ToList()
        )).ToList();
    }
}
