namespace Iemas.Application.Departments.Dtos;

public record DepartmentDto(
    Guid Id,
    string Name,
    string? Description,
    Guid? ManagerEmployeeId,
    string? ManagerEmployeeName);

public record CreateDepartmentRequest(string Name, string? Description, Guid? ManagerEmployeeId);

public record UpdateDepartmentRequest(string Name, string? Description, Guid? ManagerEmployeeId);
