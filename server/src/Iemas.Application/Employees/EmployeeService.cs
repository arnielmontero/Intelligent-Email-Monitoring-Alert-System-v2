using Iemas.Application.Common;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Employees.Dtos;
using Iemas.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Employees;

public class EmployeeService
{
    private readonly IAppDbContext _db;
    private readonly IAuditService _auditService;

    public EmployeeService(IAppDbContext db, IAuditService auditService)
    {
        _db = db;
        _auditService = auditService;
    }

    public async Task<List<EmployeeDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        return await ProjectedQuery().ToListAsync(cancellationToken);
    }

    public async Task<EmployeeDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        // Filter on the entity query before projecting into the DTO record — EF Core fails to
        // reliably translate a predicate/order applied after a record-constructing Select (found
        // via live Docker verification; see Phase 2 bugs log in the progress tracker).
        return await Project(_db.Employees.AsNoTracking().Where(e => e.Id == id)).FirstOrDefaultAsync(cancellationToken);
    }

    private IQueryable<EmployeeDto> ProjectedQuery()
    {
        return Project(_db.Employees.AsNoTracking().OrderBy(e => e.FullName));
    }

    private static IQueryable<EmployeeDto> Project(IQueryable<Employee> source)
    {
        return source.Select(e => new EmployeeDto(
            e.Id,
            e.FullName,
            e.Email,
            e.IsActive,
            e.DepartmentId,
            e.Department != null ? e.Department.Name : null,
            e.SupervisorEmployeeId,
            e.SupervisorEmployee != null ? e.SupervisorEmployee.FullName : null,
            e.Department != null ? e.Department.ManagerEmployeeId : null,
            e.Department != null && e.Department.ManagerEmployee != null ? e.Department.ManagerEmployee.FullName : null));
    }

    public async Task<Result<EmployeeDto>> CreateAsync(CreateEmployeeRequest request, CancellationToken cancellationToken)
    {
        var fullName = request.FullName.Trim();
        var email = request.Email.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(fullName))
        {
            return Result<EmployeeDto>.Failure("Full name is required.");
        }

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            return Result<EmployeeDto>.Failure("A valid email address is required.");
        }

        if (await _db.Employees.AnyAsync(e => e.Email == email, cancellationToken))
        {
            return Result<EmployeeDto>.Failure("An employee with this email already exists.");
        }

        if (request.DepartmentId is not null
            && !await _db.Departments.AnyAsync(d => d.Id == request.DepartmentId, cancellationToken))
        {
            return Result<EmployeeDto>.Failure("Department not found.");
        }

        if (request.SupervisorEmployeeId is not null
            && !await _db.Employees.AnyAsync(e => e.Id == request.SupervisorEmployeeId, cancellationToken))
        {
            return Result<EmployeeDto>.Failure("Supervisor employee not found.");
        }

        var employee = new Employee
        {
            FullName = fullName,
            Email = email,
            DepartmentId = request.DepartmentId,
            SupervisorEmployeeId = request.SupervisorEmployeeId
        };

        _db.Employees.Add(employee);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.LogAsync("EMPLOYEE_CREATED", "Employee", employee.Id.ToString(), employee.Email, cancellationToken);

        return Result<EmployeeDto>.Success(await GetByIdAsync(employee.Id, cancellationToken) ?? throw new InvalidOperationException());
    }

    public async Task<Result<EmployeeDto>> UpdateAsync(Guid id, UpdateEmployeeRequest request, CancellationToken cancellationToken)
    {
        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (employee is null)
        {
            return Result<EmployeeDto>.Failure("Employee not found.");
        }

        var fullName = request.FullName.Trim();
        var email = request.Email.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(fullName))
        {
            return Result<EmployeeDto>.Failure("Full name is required.");
        }

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            return Result<EmployeeDto>.Failure("A valid email address is required.");
        }

        if (await _db.Employees.AnyAsync(e => e.Email == email && e.Id != id, cancellationToken))
        {
            return Result<EmployeeDto>.Failure("An employee with this email already exists.");
        }

        if (request.DepartmentId is not null
            && !await _db.Departments.AnyAsync(d => d.Id == request.DepartmentId, cancellationToken))
        {
            return Result<EmployeeDto>.Failure("Department not found.");
        }

        if (request.SupervisorEmployeeId is not null)
        {
            if (request.SupervisorEmployeeId == id)
            {
                return Result<EmployeeDto>.Failure("An employee cannot be their own supervisor.");
            }

            if (!await _db.Employees.AnyAsync(e => e.Id == request.SupervisorEmployeeId, cancellationToken))
            {
                return Result<EmployeeDto>.Failure("Supervisor employee not found.");
            }

            if (await WouldCreateSupervisorCycleAsync(id, request.SupervisorEmployeeId.Value, cancellationToken))
            {
                return Result<EmployeeDto>.Failure("This assignment would create a circular supervisor chain, which would break escalation resolution.");
            }
        }

        employee.FullName = fullName;
        employee.Email = email;
        employee.IsActive = request.IsActive;
        employee.DepartmentId = request.DepartmentId;
        employee.SupervisorEmployeeId = request.SupervisorEmployeeId;
        employee.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.LogAsync("EMPLOYEE_UPDATED", "Employee", employee.Id.ToString(), employee.Email, cancellationToken);

        return Result<EmployeeDto>.Success(await GetByIdAsync(employee.Id, cancellationToken) ?? throw new InvalidOperationException());
    }

    /// <summary>
    /// Requirements §12, §58 — escalation resolution walks Employee → Supervisor → Manager.
    /// A cycle in that chain would make escalation recipient resolution loop forever, so it must
    /// be rejected at write time rather than discovered at escalation time.
    /// </summary>
    private async Task<bool> WouldCreateSupervisorCycleAsync(Guid employeeId, Guid proposedSupervisorId, CancellationToken cancellationToken)
    {
        var currentId = proposedSupervisorId;
        var visited = new HashSet<Guid>();

        while (true)
        {
            if (currentId == employeeId)
            {
                return true;
            }

            if (!visited.Add(currentId))
            {
                // Pre-existing cycle unrelated to this edit; do not block this operation on it.
                return false;
            }

            var next = await _db.Employees
                .Where(e => e.Id == currentId)
                .Select(e => e.SupervisorEmployeeId)
                .FirstOrDefaultAsync(cancellationToken);

            if (next is null)
            {
                return false;
            }

            currentId = next.Value;
        }
    }

    public async Task<Result<bool>> DeactivateAsync(Guid id, CancellationToken cancellationToken)
    {
        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (employee is null)
        {
            return Result<bool>.Failure("Employee not found.");
        }

        employee.IsActive = false;
        employee.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.LogAsync("EMPLOYEE_DEACTIVATED", "Employee", id.ToString(), employee.Email, cancellationToken);

        return Result<bool>.Success(true);
    }
}
