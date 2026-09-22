using Iemas.Application.Common;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Escalations.Dtos;
using Iemas.Domain.Escalations;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Escalations;

/// <summary>§59 "Specific Group" recipient type — CRUD for the minimal named-employee-list group concept this phase introduces.</summary>
public class EscalationGroupService
{
    private readonly IAppDbContext _db;
    private readonly IAuditService _auditService;

    public EscalationGroupService(IAppDbContext db, IAuditService auditService)
    {
        _db = db;
        _auditService = auditService;
    }

    public async Task<List<EscalationGroupDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        var groups = await _db.EscalationGroups.Include(g => g.Members).ThenInclude(m => m.Employee).OrderBy(g => g.Name).ToListAsync(cancellationToken);
        return groups.Select(ToDto).ToList();
    }

    public async Task<Result<EscalationGroupDto>> CreateAsync(SaveEscalationGroupRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return Result<EscalationGroupDto>.Failure("Name is required.");
        if (await _db.EscalationGroups.AnyAsync(g => g.Name == request.Name, cancellationToken))
        {
            return Result<EscalationGroupDto>.Failure("A group with this name already exists.");
        }

        var group = new EscalationGroup { Name = request.Name };
        foreach (var employeeId in request.EmployeeIds.Distinct())
        {
            if (!await _db.Employees.AnyAsync(e => e.Id == employeeId, cancellationToken))
            {
                return Result<EscalationGroupDto>.Failure($"Employee {employeeId} not found.");
            }
            group.Members.Add(new EscalationGroupMember { EmployeeId = employeeId });
        }

        _db.EscalationGroups.Add(group);
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("EscalationGroup.Created", "EscalationGroup", group.Id.ToString(), $"Created group \"{group.Name}\" with {group.Members.Count} member(s).", cancellationToken);

        var reloaded = await _db.EscalationGroups.Include(g => g.Members).ThenInclude(m => m.Employee).FirstAsync(g => g.Id == group.Id, cancellationToken);
        return Result<EscalationGroupDto>.Success(ToDto(reloaded));
    }

    public async Task<Result<EscalationGroupDto>> UpdateAsync(Guid id, SaveEscalationGroupRequest request, CancellationToken cancellationToken)
    {
        var group = await _db.EscalationGroups.Include(g => g.Members).FirstOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (group is null) return Result<EscalationGroupDto>.Failure("Group not found.");

        if (await _db.EscalationGroups.AnyAsync(g => g.Id != id && g.Name == request.Name, cancellationToken))
        {
            return Result<EscalationGroupDto>.Failure("A group with this name already exists.");
        }

        group.Name = request.Name;
        group.UpdatedAt = DateTimeOffset.UtcNow;
        group.Members.Clear();
        foreach (var employeeId in request.EmployeeIds.Distinct())
        {
            if (!await _db.Employees.AnyAsync(e => e.Id == employeeId, cancellationToken))
            {
                return Result<EscalationGroupDto>.Failure($"Employee {employeeId} not found.");
            }
            group.Members.Add(new EscalationGroupMember { EmployeeId = employeeId });
        }

        await _db.SaveChangesAsync(cancellationToken);

        var reloaded = await _db.EscalationGroups.Include(g => g.Members).ThenInclude(m => m.Employee).FirstAsync(g => g.Id == id, cancellationToken);
        return Result<EscalationGroupDto>.Success(ToDto(reloaded));
    }

    public async Task<Result<bool>> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var group = await _db.EscalationGroups.FirstOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (group is null) return Result<bool>.Failure("Group not found.");

        var inUse = await _db.EscalationLevels.AnyAsync(l => l.SpecificGroupId == id, cancellationToken);
        if (inUse) return Result<bool>.Failure("This group is used by one or more Escalation Levels; remove it from those levels first.");

        _db.EscalationGroups.Remove(group);
        await _db.SaveChangesAsync(cancellationToken);
        return Result<bool>.Success(true);
    }

    private static EscalationGroupDto ToDto(EscalationGroup g) => new(
        g.Id, g.Name, g.Members.Select(m => new EscalationGroupMemberDto(m.EmployeeId, m.Employee.FullName)).ToList());
}
