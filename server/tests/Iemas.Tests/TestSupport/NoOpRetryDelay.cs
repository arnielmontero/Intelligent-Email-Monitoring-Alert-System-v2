using Iemas.Application.Common.Interfaces;

namespace Iemas.Tests.TestSupport;

/// <summary>Records requested retry delays without actually waiting, so retry/backoff tests run at unit-test speed.</summary>
public class NoOpRetryDelay : IRetryDelay
{
    public List<TimeSpan> RequestedDelays { get; } = new();

    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        RequestedDelays.Add(delay);
        return Task.CompletedTask;
    }
}
