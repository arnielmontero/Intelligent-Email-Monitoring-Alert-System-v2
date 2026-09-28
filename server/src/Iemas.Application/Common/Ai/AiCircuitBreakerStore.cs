using System.Collections.Concurrent;

namespace Iemas.Application.Common.Ai;

/// <summary>Requirements §82/§83, Phase 10 hardening — see the circuit breaker design in the Build Progress Tracker for the full rationale.</summary>
public enum CircuitState
{
    Closed,
    Open,
    HalfOpen,
}

/// <summary>What happened at the end of one logical classification attempt against a model — the
/// only thing the circuit breaker ever sees. Internal retries within that attempt are invisible to
/// the circuit; only the final outcome counts (design decision — see tracker).</summary>
public enum CircuitOutcome
{
    Success,
    CountedFailure,

    /// <summary>AuthenticationFailure/InvalidRequest — never affects circuit state (design decision — see tracker).</summary>
    UncountedFailure,
}

public record CircuitTransition(string Provider, string ModelIdentifier, CircuitState From, CircuitState To, int ConsecutiveFailures, TimeSpan? CooldownDuration);

/// <summary>
/// Requirements §82/§83, Phase 10 hardening — a per-(Provider, ModelIdentifier) circuit breaker
/// sitting in front of the existing per-model retry/fallback loop in
/// <c>EmailClassificationService</c>. A pure, I/O-free state machine driven by <see cref="TimeProvider"/>
/// (the .NET 8 built-in time abstraction, not a bespoke one) so cooldown-expiry behavior is directly
/// unit-testable with <c>FakeTimeProvider</c>/a manually-advanced provider, the same "extract the
/// part that can be a pure function" pattern already used for <c>ImapFailureClassifier</c> and
/// <c>ClassificationDecisionPolicy</c>. Must be registered as a singleton — see the class doc on
/// <c>EmailClassificationService</c>'s constructor for why (it is itself scoped, so circuit state
/// cannot live on it).
///
/// Thread-safety: <see cref="ConcurrentDictionary{TKey,TValue}"/> plus a per-entry lock guards every
/// state transition, so the HALF-OPEN "exactly one probe" guarantee holds under genuinely concurrent
/// callers (the scheduled Hangfire job racing a manual API trigger), not just under the job-level
/// <c>DisableConcurrentExecution</c> guard, which is complementary defense-in-depth, not the sole guarantee.
/// </summary>
public class AiCircuitBreakerStore
{
    private const int FailureThreshold = 3;
    private static readonly TimeSpan InitialCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<(string Provider, string ModelIdentifier), CircuitEntry> _entries = new();

    public AiCircuitBreakerStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    private sealed class CircuitEntry
    {
        public readonly object Lock = new();
        public CircuitState State = CircuitState.Closed;
        public int ConsecutiveFailures;
        public TimeSpan CurrentCooldown = InitialCooldown;
        public DateTimeOffset? OpenedUntil;

        /// <summary>True while a HALF-OPEN probe is in flight — guards against a second concurrent probe.</summary>
        public bool ProbeInFlight;
    }

    /// <summary>
    /// Call before attempting a model. CLOSED/HALF-OPEN-with-a-just-granted-probe-slot returns
    /// <see langword="true"/> (proceed — and if this call itself just transitioned the circuit into
    /// HALF-OPEN, the caller's upcoming attempt IS the probe, so its outcome must be reported via
    /// <see cref="ReportOutcome"/> exactly once). OPEN (or HALF-OPEN with another probe already in
    /// flight) returns <see langword="false"/> — the caller must skip this model entirely (move to
    /// fallback) without consuming any of its retry budget.
    /// </summary>
    /// <summary>Closes every circuit for a provider, e.g. after its credentials were corrected.</summary>
    public void ResetProvider(string provider)
    {
        foreach (var key in _entries.Keys.Where(k => k.Provider == provider).ToList())
        {
            _entries.TryRemove(key, out _);
        }
    }

    public bool TryAcquire(string provider, string modelIdentifier, out List<CircuitTransition> transitions)
    {
        transitions = new List<CircuitTransition>();
        var entry = _entries.GetOrAdd((provider, modelIdentifier), _ => new CircuitEntry());

        lock (entry.Lock)
        {
            switch (entry.State)
            {
                case CircuitState.Closed:
                    return true;

                case CircuitState.Open:
                    if (entry.OpenedUntil is DateTimeOffset until && _timeProvider.GetUtcNow() >= until)
                    {
                        // Cooldown elapsed — lazily transition to HALF-OPEN and this caller claims the one probe slot.
                        entry.State = CircuitState.HalfOpen;
                        entry.ProbeInFlight = true;
                        transitions.Add(new CircuitTransition(provider, modelIdentifier, CircuitState.Open, CircuitState.HalfOpen, entry.ConsecutiveFailures, null));
                        return true;
                    }
                    return false;

                case CircuitState.HalfOpen:
                    if (entry.ProbeInFlight)
                    {
                        // Another caller already owns the single probe — treat exactly like OPEN.
                        return false;
                    }
                    entry.ProbeInFlight = true;
                    return true;

                default:
                    return true;
            }
        }
    }

    /// <summary>
    /// Reports the final outcome of one logical classification attempt (after the existing
    /// retry/fallback loop has already run its course for this model) — never per internal retry.
    /// Must be called exactly once per <see cref="TryAcquire"/> that returned <see langword="true"/>.
    /// </summary>
    public List<CircuitTransition> ReportOutcome(string provider, string modelIdentifier, CircuitOutcome outcome)
    {
        var transitions = new List<CircuitTransition>();
        if (!_entries.TryGetValue((provider, modelIdentifier), out var entry))
        {
            return transitions;
        }

        lock (entry.Lock)
        {
            var wasProbing = entry.State == CircuitState.HalfOpen;
            entry.ProbeInFlight = false;

            if (outcome == CircuitOutcome.UncountedFailure)
            {
                // AuthenticationFailure/InvalidRequest never affects circuit state (design decision).
                // If this happened to be a probe, release the slot without resolving OPEN/CLOSED —
                // the next attempt gets another chance at the single probe, since this one told us
                // nothing about the model's transient health.
                return transitions;
            }

            if (outcome == CircuitOutcome.Success)
            {
                if (entry.State != CircuitState.Closed)
                {
                    transitions.Add(new CircuitTransition(provider, modelIdentifier, entry.State, CircuitState.Closed, 0, null));
                }
                entry.State = CircuitState.Closed;
                entry.ConsecutiveFailures = 0;
                entry.CurrentCooldown = InitialCooldown;
                entry.OpenedUntil = null;
                return transitions;
            }

            // CountedFailure.
            if (wasProbing)
            {
                // A failed probe returns straight to OPEN with the next cooldown step, regardless
                // of the pre-open failure count — the probe itself doesn't get ordinary retry
                // treatment (design decision).
                entry.CurrentCooldown = NextCooldown(entry.CurrentCooldown);
                entry.OpenedUntil = _timeProvider.GetUtcNow() + entry.CurrentCooldown;
                entry.State = CircuitState.Open;
                transitions.Add(new CircuitTransition(provider, modelIdentifier, CircuitState.HalfOpen, CircuitState.Open, entry.ConsecutiveFailures, entry.CurrentCooldown));
                return transitions;
            }

            entry.ConsecutiveFailures++;
            if (entry.State == CircuitState.Closed && entry.ConsecutiveFailures >= FailureThreshold)
            {
                entry.OpenedUntil = _timeProvider.GetUtcNow() + entry.CurrentCooldown;
                entry.State = CircuitState.Open;
                transitions.Add(new CircuitTransition(provider, modelIdentifier, CircuitState.Closed, CircuitState.Open, entry.ConsecutiveFailures, entry.CurrentCooldown));
            }

            return transitions;
        }
    }

    /// <summary>Read-only snapshot for diagnostics/tests — never used to make a retry decision (that's <see cref="TryAcquire"/>'s job).</summary>
    public CircuitState GetState(string provider, string modelIdentifier) =>
        _entries.TryGetValue((provider, modelIdentifier), out var entry) ? entry.State : CircuitState.Closed;

    /// <summary>
    /// Read-only snapshot of every model this process has ever seen a classification attempt for,
    /// with its current state — for observability (health checks, diagnostics), never for retry
    /// decisions. A model never attempted since the last restart simply won't appear here (it is
    /// implicitly CLOSED, same as <see cref="GetState"/> would report for an unknown key).
    /// </summary>
    public IReadOnlyList<(string Provider, string ModelIdentifier, CircuitState State)> GetSnapshot() =>
        _entries.Select(kvp => (kvp.Key.Provider, kvp.Key.ModelIdentifier, kvp.Value.State)).ToList();

    private static TimeSpan NextCooldown(TimeSpan current)
    {
        var doubled = current * 2;
        return doubled > MaxCooldown ? MaxCooldown : doubled;
    }
}
