using Iemas.Application.Common;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Common.Security;
using Iemas.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Operations;

public record PauseControlDto(
    string Control,
    string Label,
    string Description,
    bool IsPaused,
    string? Reason,
    string? ChangedByEmail,
    DateTimeOffset? ChangedAt);

public record SetPauseRequest(bool IsPaused, string? Reason);

public static class EmergencyPauseQueries
{
    /// <summary>Checked by each engine at the start of every run, so a pause takes effect on the next tick without a restart.</summary>
    public static Task<bool> IsPausedAsync(this IAppDbContext db, PauseControl control, CancellationToken cancellationToken) =>
        db.EmergencyPauseControls.AnyAsync(c => c.Control == control && c.IsPaused, cancellationToken);
}

/// <summary>
/// Requirements §91 — Emergency Pause. Every change is permission-controlled (controller policy),
/// audited, timestamped and attributed to the acting user. Pausing never deletes or alters Cases.
/// </summary>
public class EmergencyPauseService
{
    private static readonly Dictionary<PauseControl, (string Label, string Description)> Definitions = new()
    {
        [PauseControl.EmailProcessing] = ("Pause Email Processing",
            "Stops mailbox polling and Case creation from classified email. New mail stays on the mail server and is fetched after resume."),
        [PauseControl.AiClassification] = ("Pause AI Classification",
            "Stops AI classification. Stored messages wait as Pending Classification and are classified after resume."),
        [PauseControl.Reminders] = ("Pause Reminders",
            "Stops the reminder engine. Due reminders stay Scheduled and are rechecked after resume (the policy's expiration window still applies)."),
        [PauseControl.Escalations] = ("Pause Escalations",
            "Stops escalation evaluation. No escalation level executes while paused."),
        [PauseControl.AgentNotifications] = ("Pause Agent Notifications",
            "Stops pushes to Windows Agents. New-case and escalation notifications are recorded as Cancelled, and the reminder engine also holds so reminders are not counted as sent while employees cannot see them."),
    };

    private readonly IAppDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditService _auditService;

    public EmergencyPauseService(IAppDbContext db, ICurrentUserService currentUser, IAuditService auditService)
    {
        _db = db;
        _currentUser = currentUser;
        _auditService = auditService;
    }

    public async Task<List<PauseControlDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        var stored = await _db.EmergencyPauseControls.AsNoTracking().ToListAsync(cancellationToken);
        return Enum.GetValues<PauseControl>()
            .Select(control => ToDto(control, stored.FirstOrDefault(s => s.Control == control)))
            .ToList();
    }

    public async Task<Result<PauseControlDto>> SetAsync(string controlName, SetPauseRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<PauseControl>(controlName, ignoreCase: true, out var control) || !Enum.IsDefined(control))
        {
            return Result<PauseControlDto>.Failure($"Unknown pause control '{controlName}'.");
        }

        var reason = request.Reason?.Trim();
        if (request.IsPaused && string.IsNullOrWhiteSpace(reason))
        {
            return Result<PauseControlDto>.Failure("A reason is required when pausing.");
        }
        if (!string.IsNullOrEmpty(reason))
        {
            if (reason.Length > 500) return Result<PauseControlDto>.Failure("Reason cannot exceed 500 characters.");
            if (InputSanitizer.ValidateFreeText("Reason", reason) is { } error) return Result<PauseControlDto>.Failure(error);
        }

        var row = await _db.EmergencyPauseControls.FirstOrDefaultAsync(c => c.Control == control, cancellationToken);
        if (row is null)
        {
            row = new EmergencyPauseControl { Control = control };
            _db.EmergencyPauseControls.Add(row);
        }

        var wasPaused = row.IsPaused;
        row.IsPaused = request.IsPaused;
        row.Reason = string.IsNullOrEmpty(reason) ? null : reason;
        row.ChangedByUserId = _currentUser.UserId;
        row.ChangedByEmail = _currentUser.Email;
        row.ChangedAt = DateTimeOffset.UtcNow;
        row.UpdatedAt = row.ChangedAt;
        await _db.SaveChangesAsync(cancellationToken);

        if (wasPaused != request.IsPaused)
        {
            await _auditService.LogAsync(
                request.IsPaused ? "EMERGENCY_PAUSE_ENABLED" : "EMERGENCY_PAUSE_DISABLED",
                "EmergencyPause",
                control.ToString(),
                row.Reason,
                cancellationToken);
        }

        return Result<PauseControlDto>.Success(ToDto(control, row));
    }

    private static PauseControlDto ToDto(PauseControl control, EmergencyPauseControl? row)
    {
        var (label, description) = Definitions[control];
        return new PauseControlDto(
            control.ToString(), label, description,
            row?.IsPaused ?? false, row?.Reason, row?.ChangedByEmail, row?.ChangedAt);
    }
}
