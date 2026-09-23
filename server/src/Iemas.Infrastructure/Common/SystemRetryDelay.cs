using Iemas.Application.Common.Interfaces;

namespace Iemas.Infrastructure.Common;

/// <summary>Real implementation of <see cref="IRetryDelay"/> — see that interface for why this indirection exists.</summary>
public class SystemRetryDelay : IRetryDelay
{
    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay > TimeSpan.Zero ? Task.Delay(delay, cancellationToken) : Task.CompletedTask;
}
