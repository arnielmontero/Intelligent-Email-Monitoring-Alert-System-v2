namespace Iemas.Domain.Identity;

/// <summary>
/// Requirements §85 — RBAC suggested roles.
/// </summary>
public static class SystemRole
{
    public const string SuperAdministrator = "SuperAdministrator";
    public const string Administrator = "Administrator";
    public const string SupervisorManager = "SupervisorManager";
    public const string Employee = "Employee";
    public const string Auditor = "Auditor";

    public static readonly string[] All =
    {
        SuperAdministrator,
        Administrator,
        SupervisorManager,
        Employee,
        Auditor
    };
}
