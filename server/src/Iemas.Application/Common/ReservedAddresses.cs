using Iemas.Application.Common.Interfaces;
using Iemas.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Common;

/// <summary>
/// A Super Administrator's sign-in address (e.g. admin@sawo.com) is for access to the CMS only: it never receives
/// customer email and belongs to no employee, so no employee or mailbox may use it.
/// </summary>
public static class ReservedAddresses
{
    public const string SuperAdministratorMessage =
        "This address is a Super Administrator sign-in. It is used only for access to IEMAS and can't also be an employee or a mailbox.";

    public static Task<bool> IsSuperAdministratorLoginAsync(IAppDbContext db, string email, CancellationToken cancellationToken)
    {
        var normalized = email.Trim().ToLower();
        return db.UserRoles.AnyAsync(ur => ur.User.Email.ToLower() == normalized && ur.Role.Name == SystemRole.SuperAdministrator, cancellationToken);
    }
}
