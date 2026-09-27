using System.Net.Mail;
using Iemas.Application.Common;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Common.Security;
using Iemas.Application.Operations;
using Iemas.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Users;

public record UserDto(
    Guid Id,
    string Email,
    string DisplayName,
    bool IsActive,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset CreatedAt,
    Guid? EmployeeId,
    string? EmployeeName,
    List<string> Roles);

public record CreateUserRequest(string Email, string DisplayName, string Password, List<string> Roles, Guid? EmployeeId);

public record UpdateUserRequest(string DisplayName, List<string> Roles, Guid? EmployeeId);

public record ResetPasswordRequest(string NewPassword);

public record ChangeOwnPasswordRequest(string CurrentPassword, string NewPassword);

public record RoleDto(string Name, string Label, string Description, List<string> Permissions);

/// <summary>
/// Requirements §85 RBAC ("User management" permission) and §84 (secure password handling,
/// credential revocation). Guards against privilege escalation: only a Super Administrator may
/// grant the Super Administrator role or modify an account that holds it, nobody can deactivate
/// themselves, and the last active Super Administrator can never be removed.
/// </summary>
public class UserManagementService
{
    /// <summary>Mirrors the controller authorization policies actually in force, so the CMS matrix never overstates access.</summary>
    public static readonly IReadOnlyList<RoleDto> RoleCatalog = new List<RoleDto>
    {
        new(SystemRole.SuperAdministrator, "Super Administrator", "Full access.", new()
        {
            "Everything an Administrator can do",
            "Grant or revoke the Super Administrator role",
            "Manage Super Administrator accounts",
        }),
        new(SystemRole.Administrator, "Administrator", "System configuration and Case administration.", new()
        {
            "Email configuration (accounts, outbound, intake)",
            "AI configuration (models, classification profiles)",
            "Case administration (workflow, complete/cancel, reply verification)",
            "Reminder and escalation policy management",
            "Windows Agent management",
            "User management (except Super Administrator accounts)",
            "Notification templates",
            "System settings and Emergency Pause",
            "Everything a Supervisor / Manager can view",
        }),
        new(SystemRole.SupervisorManager, "Supervisor / Manager", "Authorized team Cases and escalation information.", new()
        {
            "View Cases, Case History, reminders and escalations",
            "View notifications and Employee Activity",
            "Everything an Auditor can view",
        }),
        new(SystemRole.Auditor, "Auditor / Read-only", "Authorized history and audit access.", new()
        {
            "Dashboard and System Health",
            "Audit Log",
            "Employee Activity",
            "Employee and department directory",
            "Emergency Pause status (read-only)",
        }),
        new(SystemRole.Employee, "Employee", "Own Cases, notifications, Agent actions.", new()
        {
            "No CMS pages — works through the Windows Agent",
        }),
    };

    private readonly IAppDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditService _auditService;

    public UserManagementService(IAppDbContext db, IPasswordHasher passwordHasher, ICurrentUserService currentUser, IAuditService auditService)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _currentUser = currentUser;
        _auditService = auditService;
    }

    private bool CallerIsSuperAdministrator => _currentUser.Roles.Contains(SystemRole.SuperAdministrator);

    public async Task<List<UserDto>> ListAsync(CancellationToken cancellationToken)
    {
        var users = await _db.Users.AsNoTracking()
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .Include(u => u.Employee)
            .OrderBy(u => u.Email)
            .ToListAsync(cancellationToken);
        return users.Select(ToDto).ToList();
    }

    public async Task<Result<UserDto>> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!IsValidEmail(email)) return Result<UserDto>.Failure("A valid email address is required.");
        if (await _db.Users.AnyAsync(u => u.Email == email, cancellationToken)) return Result<UserDto>.Failure("A user with this email already exists.");

        var displayName = request.DisplayName?.Trim() ?? string.Empty;
        if (ValidateDisplayName(displayName) is { } nameError) return Result<UserDto>.Failure(nameError);

        var roleResult = await ResolveRolesAsync(request.Roles, cancellationToken);
        if (!roleResult.Succeeded) return Result<UserDto>.Failure(roleResult.Error!);
        if (roleResult.Value!.Any(r => r.Name == SystemRole.SuperAdministrator) && !CallerIsSuperAdministrator)
        {
            return Result<UserDto>.Failure("Only a Super Administrator can grant the Super Administrator role.");
        }

        if (await ValidateEmployeeLinkAsync(request.EmployeeId, null, cancellationToken) is { } employeeError) return Result<UserDto>.Failure(employeeError);
        if (await ValidatePasswordAsync(request.Password, cancellationToken) is { } passwordError) return Result<UserDto>.Failure(passwordError);

        var user = new User
        {
            Email = email,
            DisplayName = displayName,
            PasswordHash = _passwordHasher.Hash(request.Password),
            IsActive = true,
            EmployeeId = request.EmployeeId,
        };
        foreach (var role in roleResult.Value!)
        {
            user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        }
        _db.Users.Add(user);
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("USER_CREATED", "User", user.Id.ToString(),
            $"{email}; roles: {string.Join(", ", roleResult.Value!.Select(r => r.Name))}", cancellationToken);

        return Result<UserDto>.Success(await GetDtoAsync(user.Id, cancellationToken));
    }

    public async Task<Result<UserDto>> UpdateAsync(Guid userId, UpdateUserRequest request, CancellationToken cancellationToken)
    {
        var user = await LoadAsync(userId, cancellationToken);
        if (user is null) return Result<UserDto>.Failure("User not found.");
        if (GuardSuperAdministratorTarget(user) is { } guardError) return Result<UserDto>.Failure(guardError);

        var displayName = request.DisplayName?.Trim() ?? string.Empty;
        if (ValidateDisplayName(displayName) is { } nameError) return Result<UserDto>.Failure(nameError);

        var roleResult = await ResolveRolesAsync(request.Roles, cancellationToken);
        if (!roleResult.Succeeded) return Result<UserDto>.Failure(roleResult.Error!);
        var newRoles = roleResult.Value!;

        var hadSuper = HasRole(user, SystemRole.SuperAdministrator);
        var willHaveSuper = newRoles.Any(r => r.Name == SystemRole.SuperAdministrator);
        if (hadSuper != willHaveSuper && !CallerIsSuperAdministrator)
        {
            return Result<UserDto>.Failure("Only a Super Administrator can grant or revoke the Super Administrator role.");
        }
        if (hadSuper && !willHaveSuper && user.IsActive && await IsLastActiveSuperAdministratorAsync(user.Id, cancellationToken))
        {
            return Result<UserDto>.Failure("Cannot remove the Super Administrator role from the last active Super Administrator.");
        }

        if (await ValidateEmployeeLinkAsync(request.EmployeeId, user.Id, cancellationToken) is { } employeeError) return Result<UserDto>.Failure(employeeError);

        var oldRoles = user.UserRoles.Select(ur => ur.Role.Name).OrderBy(n => n).ToList();
        user.DisplayName = displayName;
        user.EmployeeId = request.EmployeeId;
        user.UpdatedAt = DateTimeOffset.UtcNow;

        foreach (var existing in user.UserRoles.Where(ur => newRoles.All(r => r.Id != ur.RoleId)).ToList())
        {
            user.UserRoles.Remove(existing);
            _db.UserRoles.Remove(existing);
        }
        foreach (var role in newRoles.Where(r => user.UserRoles.All(ur => ur.RoleId != r.Id)))
        {
            _db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        }
        await _db.SaveChangesAsync(cancellationToken);

        var roleNames = newRoles.Select(r => r.Name).OrderBy(n => n).ToList();
        var details = oldRoles.SequenceEqual(roleNames)
            ? $"{user.Email}; details updated"
            : $"{user.Email}; roles: [{string.Join(", ", oldRoles)}] -> [{string.Join(", ", roleNames)}]";
        await _auditService.LogAsync("USER_UPDATED", "User", user.Id.ToString(), details, cancellationToken);

        return Result<UserDto>.Success(await GetDtoAsync(user.Id, cancellationToken));
    }

    public async Task<Result<UserDto>> SetActiveAsync(Guid userId, bool isActive, CancellationToken cancellationToken)
    {
        var user = await LoadAsync(userId, cancellationToken);
        if (user is null) return Result<UserDto>.Failure("User not found.");
        if (GuardSuperAdministratorTarget(user) is { } guardError) return Result<UserDto>.Failure(guardError);

        if (!isActive)
        {
            if (user.Id == _currentUser.UserId) return Result<UserDto>.Failure("You cannot deactivate your own account.");
            if (HasRole(user, SystemRole.SuperAdministrator) && await IsLastActiveSuperAdministratorAsync(user.Id, cancellationToken))
            {
                return Result<UserDto>.Failure("Cannot deactivate the last active Super Administrator.");
            }
        }

        if (user.IsActive == isActive) return Result<UserDto>.Success(ToDto(user));

        user.IsActive = isActive;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        if (!isActive)
        {
            await RevokeRefreshTokensAsync(user.Id, cancellationToken);
        }
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(isActive ? "USER_REACTIVATED" : "USER_DEACTIVATED", "User", user.Id.ToString(), user.Email, cancellationToken);
        return Result<UserDto>.Success(ToDto(user));
    }

    public async Task<Result<bool>> ResetPasswordAsync(Guid userId, ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        var user = await LoadAsync(userId, cancellationToken);
        if (user is null) return Result<bool>.Failure("User not found.");
        if (GuardSuperAdministratorTarget(user) is { } guardError) return Result<bool>.Failure(guardError);
        if (await ValidatePasswordAsync(request.NewPassword, cancellationToken) is { } passwordError) return Result<bool>.Failure(passwordError);

        user.PasswordHash = _passwordHasher.Hash(request.NewPassword);
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await RevokeRefreshTokensAsync(user.Id, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("USER_PASSWORD_RESET", "User", user.Id.ToString(), $"{user.Email}; all sessions revoked", cancellationToken);
        return Result<bool>.Success(true);
    }

    public async Task<Result<bool>> ChangeOwnPasswordAsync(ChangeOwnPasswordRequest request, CancellationToken cancellationToken)
    {
        if (_currentUser.UserId is not Guid userId) return Result<bool>.Failure("Not signed in.");
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null || !user.IsActive) return Result<bool>.Failure("User not found.");

        if (string.IsNullOrEmpty(request.CurrentPassword) || !_passwordHasher.Verify(request.CurrentPassword, user.PasswordHash))
        {
            await _auditService.LogAsync("USER_PASSWORD_CHANGE_FAILED", "User", user.Id.ToString(), "Current password did not match", cancellationToken);
            return Result<bool>.Failure("Current password is incorrect.");
        }
        if (request.NewPassword == request.CurrentPassword) return Result<bool>.Failure("New password must differ from the current password.");
        if (await ValidatePasswordAsync(request.NewPassword, cancellationToken) is { } passwordError) return Result<bool>.Failure(passwordError);

        user.PasswordHash = _passwordHasher.Hash(request.NewPassword);
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await RevokeRefreshTokensAsync(user.Id, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("USER_PASSWORD_CHANGED", "User", user.Id.ToString(), $"{user.Email}; other sessions revoked", cancellationToken);
        return Result<bool>.Success(true);
    }

    private string? GuardSuperAdministratorTarget(User user) =>
        HasRole(user, SystemRole.SuperAdministrator) && !CallerIsSuperAdministrator
            ? "Only a Super Administrator can modify a Super Administrator account."
            : null;

    private async Task<Result<List<Role>>> ResolveRolesAsync(List<string>? requested, CancellationToken cancellationToken)
    {
        var names = (requested ?? new List<string>()).Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).Distinct().ToList();
        if (names.Count == 0) return Result<List<Role>>.Failure("At least one role is required.");

        var unknown = names.Where(n => !SystemRole.All.Contains(n)).ToList();
        if (unknown.Count > 0) return Result<List<Role>>.Failure($"Unknown role(s): {string.Join(", ", unknown)}.");

        var roles = await _db.Roles.Where(r => names.Contains(r.Name)).ToListAsync(cancellationToken);
        return roles.Count == names.Count
            ? Result<List<Role>>.Success(roles)
            : Result<List<Role>>.Failure("One or more roles are not configured in the database.");
    }

    private async Task<string?> ValidateEmployeeLinkAsync(Guid? employeeId, Guid? excludingUserId, CancellationToken cancellationToken)
    {
        if (employeeId is not Guid id) return null;
        if (!await _db.Employees.AnyAsync(e => e.Id == id, cancellationToken)) return "Linked employee not found.";
        return await _db.Users.AnyAsync(u => u.EmployeeId == id && u.Id != excludingUserId, cancellationToken)
            ? "That employee is already linked to another user."
            : null;
    }

    private async Task<string?> ValidatePasswordAsync(string? password, CancellationToken cancellationToken)
    {
        var minLength = await SystemSettingsService.GetIntAsync(_db, SystemSettingKeys.PasswordMinLength, cancellationToken);
        if (string.IsNullOrEmpty(password) || password.Length < minLength) return $"Password must be at least {minLength} characters.";
        if (password.Length > 128) return "Password cannot exceed 128 characters.";
        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit)) return "Password must contain at least one letter and one digit.";
        return null;
    }

    private static string? ValidateDisplayName(string displayName)
    {
        if (displayName.Length == 0 || displayName.Length > 200) return "Display name must be between 1 and 200 characters.";
        return InputSanitizer.ValidateFreeText("Display name", displayName);
    }

    private static bool IsValidEmail(string email) =>
        email.Length is > 3 and <= 256 && MailAddress.TryCreate(email, out var parsed) && parsed.Address == email;

    private static bool HasRole(User user, string role) => user.UserRoles.Any(ur => ur.Role.Name == role);

    private async Task<bool> IsLastActiveSuperAdministratorAsync(Guid userId, CancellationToken cancellationToken) =>
        !await _db.Users.AnyAsync(u => u.Id != userId && u.IsActive
            && u.UserRoles.Any(ur => ur.Role.Name == SystemRole.SuperAdministrator), cancellationToken);

    private async Task RevokeRefreshTokensAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var tokens = await _db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var token in tokens)
        {
            token.RevokedAt = now;
        }
    }

    private Task<User?> LoadAsync(Guid userId, CancellationToken cancellationToken) =>
        _db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .Include(u => u.Employee)
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

    private async Task<UserDto> GetDtoAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await _db.Users.AsNoTracking()
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .Include(u => u.Employee)
            .FirstAsync(u => u.Id == userId, cancellationToken);
        return ToDto(user);
    }

    private static UserDto ToDto(User user) => new(
        user.Id, user.Email, user.DisplayName, user.IsActive, user.LastLoginAt, user.CreatedAt,
        user.EmployeeId, user.Employee?.FullName,
        user.UserRoles.Select(ur => ur.Role.Name).OrderBy(n => Array.IndexOf(SystemRole.All, n)).ToList());
}
