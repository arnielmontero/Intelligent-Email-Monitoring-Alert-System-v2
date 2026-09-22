using Iemas.Domain.Common;

namespace Iemas.Domain.Identity;

/// <summary>
/// Requirements §11, §12 — Employee is separate from User (login) and Agent (device).
/// Case ownership is always at the Employee level.
/// </summary>
public class Employee : Entity
{
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    public Guid? DepartmentId { get; set; }
    public Department? Department { get; set; }

    /// <summary>Direct supervisor, used for §58 escalation Level 1 recipient resolution.</summary>
    public Guid? SupervisorEmployeeId { get; set; }
    public Employee? SupervisorEmployee { get; set; }
}
