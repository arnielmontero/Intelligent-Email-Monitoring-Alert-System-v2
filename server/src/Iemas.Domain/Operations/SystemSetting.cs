using Iemas.Domain.Common;

namespace Iemas.Domain.Operations;

/// <summary>A stored override for one entry of the fixed system settings catalog. Absent row = catalog default.</summary>
public class SystemSetting : Entity
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;

    public Guid? UpdatedByUserId { get; set; }
    public string? UpdatedByEmail { get; set; }
}
