using Iemas.Application.Common;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Common.Security;
using Iemas.Application.Departments.Dtos;
using Iemas.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Departments;

public class DepartmentService
{
    private readonly IAppDbContext _db;
    private readonly IAuditService _auditService;

    public DepartmentService(IAppDbContext db, IAuditService auditService)
    {
        _db = db;
        _auditService = auditService;
    }

    public async Task<List<DepartmentDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        // Order before projecting into the DTO record — EF Core fails to translate ORDER BY
        // applied after a record-constructing Select (see EmployeeService.ProjectedQuery for the
        // same issue, found via live Docker verification).
        return await _db.Departments
            .AsNoTracking()
            .OrderBy(d => d.Name)
            .Select(d => new DepartmentDto(
                d.Id,
                d.Name,
                d.Description,
                d.ManagerEmployeeId,
                d.ManagerEmployee != null ? d.ManagerEmployee.FullName : null))
            .ToListAsync(cancellationToken);
    }

    public async Task<DepartmentDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        return await _db.Departments
            .AsNoTracking()
            .Where(d => d.Id == id)
            .Select(d => new DepartmentDto(
                d.Id,
                d.Name,
                d.Description,
                d.ManagerEmployeeId,
                d.ManagerEmployee != null ? d.ManagerEmployee.FullName : null))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<Result<DepartmentDto>> CreateAsync(CreateDepartmentRequest request, CancellationToken cancellationToken)
    {
        var name = request.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result<DepartmentDto>.Failure("Department name is required.");
        }

        if (InputSanitizer.ValidateFreeText("Department name", name) is { } nameError)
        {
            return Result<DepartmentDto>.Failure(nameError);
        }

        var description = request.Description?.Trim();
        if (description is not null && InputSanitizer.ValidateFreeText("Description", description) is { } descriptionError)
        {
            return Result<DepartmentDto>.Failure(descriptionError);
        }

        if (await _db.Departments.AnyAsync(d => d.Name == name, cancellationToken))
        {
            return Result<DepartmentDto>.Failure("A department with this name already exists.");
        }

        if (request.ManagerEmployeeId is not null
            && !await _db.Employees.AnyAsync(e => e.Id == request.ManagerEmployeeId, cancellationToken))
        {
            return Result<DepartmentDto>.Failure("Manager employee not found.");
        }

        var department = new Department
        {
            Name = name,
            Description = request.Description?.Trim(),
            ManagerEmployeeId = request.ManagerEmployeeId
        };

        _db.Departments.Add(department);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.LogAsync("DEPARTMENT_CREATED", "Department", department.Id.ToString(), department.Name, cancellationToken);

        return Result<DepartmentDto>.Success(await GetByIdAsync(department.Id, cancellationToken) ?? throw new InvalidOperationException());
    }

    public async Task<Result<DepartmentDto>> UpdateAsync(Guid id, UpdateDepartmentRequest request, CancellationToken cancellationToken)
    {
        var department = await _db.Departments.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (department is null)
        {
            return Result<DepartmentDto>.Failure("Department not found.");
        }

        var name = request.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result<DepartmentDto>.Failure("Department name is required.");
        }

        if (InputSanitizer.ValidateFreeText("Department name", name) is { } nameError)
        {
            return Result<DepartmentDto>.Failure(nameError);
        }

        var description = request.Description?.Trim();
        if (description is not null && InputSanitizer.ValidateFreeText("Description", description) is { } descriptionError)
        {
            return Result<DepartmentDto>.Failure(descriptionError);
        }

        if (await _db.Departments.AnyAsync(d => d.Name == name && d.Id != id, cancellationToken))
        {
            return Result<DepartmentDto>.Failure("A department with this name already exists.");
        }

        if (request.ManagerEmployeeId is not null
            && !await _db.Employees.AnyAsync(e => e.Id == request.ManagerEmployeeId, cancellationToken))
        {
            return Result<DepartmentDto>.Failure("Manager employee not found.");
        }

        department.Name = name;
        department.Description = request.Description?.Trim();
        department.ManagerEmployeeId = request.ManagerEmployeeId;
        department.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.LogAsync("DEPARTMENT_UPDATED", "Department", department.Id.ToString(), department.Name, cancellationToken);

        return Result<DepartmentDto>.Success(await GetByIdAsync(department.Id, cancellationToken) ?? throw new InvalidOperationException());
    }

    public async Task<Result<bool>> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var department = await _db.Departments.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (department is null)
        {
            return Result<bool>.Failure("Department not found.");
        }

        if (await _db.Employees.AnyAsync(e => e.DepartmentId == id, cancellationToken))
        {
            return Result<bool>.Failure("Cannot delete a department that still has employees assigned to it.");
        }

        _db.Departments.Remove(department);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.LogAsync("DEPARTMENT_DELETED", "Department", id.ToString(), department.Name, cancellationToken);

        return Result<bool>.Success(true);
    }
}
