using Iemas.Application.Common.Interfaces;

namespace Iemas.Tests.TestSupport;

/// <summary>Captures every push a test triggers, so tests can assert exactly what would have been sent to the Windows Agent without a real SignalR connection.</summary>
public class FakeAgentNotificationDispatcher : IAgentNotificationDispatcher
{
    public List<(Guid EmployeeId, AgentPushCommand Command)> Sent { get; } = new();

    /// <summary>How many connected Agents each push reports reaching; 0 simulates an employee with no Agent online.</summary>
    public int ConnectedAgentCount { get; set; } = 1;

    public Task<int> NotifyEmployeeAsync(Guid employeeId, AgentPushCommand command, CancellationToken cancellationToken)
    {
        Sent.Add((employeeId, command));
        return Task.FromResult(ConnectedAgentCount);
    }
}
