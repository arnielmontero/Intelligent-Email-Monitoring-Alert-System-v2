using Iemas.Domain.Common;

namespace Iemas.Domain.Identity;

/// <summary>
/// A CMS login account. May optionally be linked to an Employee record.
/// Requirements §99 (Identity domain), §84 (secure password handling).
/// </summary>
public class User : Entity
{
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastLoginAt { get; set; }

    public Guid? EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
}
