using Iemas.Domain.Common;

namespace Iemas.Domain.Identity;

public class Department : Entity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public Guid? ManagerEmployeeId { get; set; }
    public Employee? ManagerEmployee { get; set; }

    public ICollection<Employee> Employees { get; set; } = new List<Employee>();
}
