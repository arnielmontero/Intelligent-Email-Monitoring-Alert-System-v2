using Iemas.Domain.Common;

namespace Iemas.Domain.Identity;

public class Role : Entity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
}
