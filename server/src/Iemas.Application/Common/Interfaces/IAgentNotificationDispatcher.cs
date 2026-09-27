namespace Iemas.Application.Common.Interfaces;

/// <summary>
/// Requirements §72 (the closed server-to-agent command vocabulary), §76 ("Database transaction →
/// Commit → SignalR notification" — persistence is always the state transition; this dispatcher is
/// called only AFTER a caller has already committed the authoritative DB state, never before/instead
/// of it), §13 (an Employee may have more than one Agent — <see cref="NotifyEmployeeAsync"/> targets
/// every currently-connected Agent belonging to that Employee, i.e. Option A "All Active Agents";
/// offline Agents simply miss the push and pick up current state on their next SYNC per §74/§75 —
/// this is explicitly non-fatal and never retried, because SignalR is a transport, not a source of
/// truth, per §76).
///
/// Implementations must never throw out of a push failure in a way that could roll back or block the
/// caller's already-committed business transaction — delivery is best-effort only.
/// </summary>
public interface IAgentNotificationDispatcher
{
    /// <summary>Pushes a §72 command to every currently-connected Agent for the given Employee. Best-effort; does not throw on delivery failure. Returns how many Agents the push was handed to.</summary>
    Task<int> NotifyEmployeeAsync(Guid employeeId, AgentPushCommand command, CancellationToken cancellationToken);
}

/// <summary>§72 command payload — deliberately just the closed set of fields the Agent needs to render a toast/update its Action Required list. Never a free-form/arbitrary payload (§8.2 "no arbitrary remote command execution").</summary>
public enum AgentPushCommandType
{
    ShowNotification = 0,
    ShowReminder = 1,
    ShowCase = 2,
    CancelNotification = 3,
    SyncRequired = 4,
    Ping = 5,
}

public record AgentPushCommand(
    AgentPushCommandType Type,
    Guid? CaseId,
    string? CaseNumber,
    string? Title,
    string? Message);
