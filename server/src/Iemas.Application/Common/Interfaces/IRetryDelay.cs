namespace Iemas.Application.Common.Interfaces;

/// <summary>
/// Phase 10 hardening — abstracts the wait between a retryable AI classification failure and the
/// next attempt, so <see cref="Task.Delay(TimeSpan, CancellationToken)"/> is never called directly
/// from application logic. Without this, a unit test exercising a multi-attempt retry path either
/// has to genuinely sleep (slowing the suite and inviting someone to "fix" that by quietly loosening
/// the retry/backoff logic itself) or the delay gets skipped in a way that silently stops proving
/// what production actually does. The real implementation (Infrastructure) awaits normally; tests
/// use a no-op fake that records the requested delays without waiting for them.
/// </summary>
public interface IRetryDelay
{
    Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken);
}
