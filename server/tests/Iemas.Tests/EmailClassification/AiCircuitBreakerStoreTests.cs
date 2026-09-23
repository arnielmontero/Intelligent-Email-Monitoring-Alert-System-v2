using Iemas.Application.Common.Ai;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iemas.Tests.EmailClassification;

/// <summary>
/// Phase 10 hardening — see the circuit breaker design in the Build Progress Tracker for the full
/// state machine rationale. A pure, I/O-free state machine driven by <see cref="TimeProvider"/>, so
/// every scenario here (including cooldown-expiry timing) is deterministic via
/// <see cref="FakeTimeProvider"/> rather than a real wall-clock wait.
/// </summary>
public class AiCircuitBreakerStoreTests
{
    private const string Provider = "OpenRouter";
    private const string Model = "openai/gpt-4o-mini";

    [Fact]
    public void TryAcquire_FreshCircuit_IsClosed_AllowsAttempt()
    {
        var store = new AiCircuitBreakerStore(new FakeTimeProvider());
        Assert.True(store.TryAcquire(Provider, Model, out _));
        Assert.Equal(CircuitState.Closed, store.GetState(Provider, Model));
    }

    /// <summary>Checklist item 1 — 3 consecutive counted failures opens the circuit.</summary>
    [Fact]
    public void ReportOutcome_ThreeConsecutiveCountedFailures_OpensCircuit()
    {
        var store = new AiCircuitBreakerStore(new FakeTimeProvider());

        store.TryAcquire(Provider, Model, out _);
        store.ReportOutcome(Provider, Model, CircuitOutcome.CountedFailure);
        Assert.Equal(CircuitState.Closed, store.GetState(Provider, Model));

        store.TryAcquire(Provider, Model, out _);
        store.ReportOutcome(Provider, Model, CircuitOutcome.CountedFailure);
        Assert.Equal(CircuitState.Closed, store.GetState(Provider, Model));

        store.TryAcquire(Provider, Model, out _);
        var transitions = store.ReportOutcome(Provider, Model, CircuitOutcome.CountedFailure);

        Assert.Equal(CircuitState.Open, store.GetState(Provider, Model));
        var opened = Assert.Single(transitions);
        Assert.Equal(CircuitState.Closed, opened.From);
        Assert.Equal(CircuitState.Open, opened.To);
        Assert.Equal(3, opened.ConsecutiveFailures);
        Assert.Equal(TimeSpan.FromSeconds(30), opened.CooldownDuration);
    }

    /// <summary>A success before reaching the threshold resets the consecutive-failure count to zero.</summary>
    [Fact]
    public void ReportOutcome_SuccessBeforeThreshold_ResetsFailureCount_CircuitStaysClosed()
    {
        var store = new AiCircuitBreakerStore(new FakeTimeProvider());

        store.TryAcquire(Provider, Model, out _);
        store.ReportOutcome(Provider, Model, CircuitOutcome.CountedFailure);
        store.TryAcquire(Provider, Model, out _);
        store.ReportOutcome(Provider, Model, CircuitOutcome.CountedFailure);
        store.TryAcquire(Provider, Model, out _);
        store.ReportOutcome(Provider, Model, CircuitOutcome.Success);

        // Two more failures after the reset must not open the circuit — the count restarted at 0.
        store.TryAcquire(Provider, Model, out _);
        store.ReportOutcome(Provider, Model, CircuitOutcome.CountedFailure);
        store.TryAcquire(Provider, Model, out _);
        store.ReportOutcome(Provider, Model, CircuitOutcome.CountedFailure);

        Assert.Equal(CircuitState.Closed, store.GetState(Provider, Model));
    }

    /// <summary>Design decision — AuthenticationFailure/InvalidRequest never count toward the threshold.</summary>
    [Fact]
    public void ReportOutcome_UncountedFailure_NeverOpensCircuit_EvenAfterManyAttempts()
    {
        var store = new AiCircuitBreakerStore(new FakeTimeProvider());

        for (var i = 0; i < 10; i++)
        {
            store.TryAcquire(Provider, Model, out _);
            store.ReportOutcome(Provider, Model, CircuitOutcome.UncountedFailure);
        }

        Assert.Equal(CircuitState.Closed, store.GetState(Provider, Model));
    }

    /// <summary>Checklist item 2 — OPEN bypasses the model entirely (TryAcquire returns false), consuming no retry budget.</summary>
    [Fact]
    public void TryAcquire_OpenCircuit_BeforeCooldownElapses_ReturnsFalse()
    {
        var time = new FakeTimeProvider();
        var store = new AiCircuitBreakerStore(time);
        OpenCircuit(store);

        Assert.False(store.TryAcquire(Provider, Model, out var transitions));
        Assert.Empty(transitions);
        Assert.Equal(CircuitState.Open, store.GetState(Provider, Model));
    }

    /// <summary>Checklist item 3 — after cooldown, exactly one caller is granted the HALF-OPEN probe.</summary>
    [Fact]
    public void TryAcquire_AfterCooldownElapses_TransitionsToHalfOpen_GrantsProbe()
    {
        var time = new FakeTimeProvider();
        var store = new AiCircuitBreakerStore(time);
        OpenCircuit(store);

        time.Advance(TimeSpan.FromSeconds(31)); // past the 30s initial cooldown

        var acquired = store.TryAcquire(Provider, Model, out var transitions);

        Assert.True(acquired);
        Assert.Equal(CircuitState.HalfOpen, store.GetState(Provider, Model));
        var transition = Assert.Single(transitions);
        Assert.Equal(CircuitState.Open, transition.From);
        Assert.Equal(CircuitState.HalfOpen, transition.To);
    }

    /// <summary>Checklist item 4 — a second concurrent caller cannot acquire a second probe while one is in flight.</summary>
    [Fact]
    public void TryAcquire_SecondCallerDuringInFlightProbe_ReturnsFalse()
    {
        var time = new FakeTimeProvider();
        var store = new AiCircuitBreakerStore(time);
        OpenCircuit(store);
        time.Advance(TimeSpan.FromSeconds(31));

        Assert.True(store.TryAcquire(Provider, Model, out _)); // first caller claims the probe
        Assert.False(store.TryAcquire(Provider, Model, out var transitions)); // second caller is refused

        Assert.Empty(transitions);
        Assert.Equal(CircuitState.HalfOpen, store.GetState(Provider, Model));
    }

    /// <summary>Checklist item 5 — a successful probe closes the circuit and resets the failure count.</summary>
    [Fact]
    public void ReportOutcome_SuccessfulProbe_ClosesCircuit_ResetsFailureCount()
    {
        var time = new FakeTimeProvider();
        var store = new AiCircuitBreakerStore(time);
        OpenCircuit(store);
        time.Advance(TimeSpan.FromSeconds(31));
        store.TryAcquire(Provider, Model, out _);

        var transitions = store.ReportOutcome(Provider, Model, CircuitOutcome.Success);

        Assert.Equal(CircuitState.Closed, store.GetState(Provider, Model));
        var transition = Assert.Single(transitions);
        Assert.Equal(CircuitState.HalfOpen, transition.From);
        Assert.Equal(CircuitState.Closed, transition.To);
        Assert.Equal(0, transition.ConsecutiveFailures);

        // Confirms the reset: 2 more failures (below the fresh threshold of 3) must not re-open it.
        store.TryAcquire(Provider, Model, out _);
        store.ReportOutcome(Provider, Model, CircuitOutcome.CountedFailure);
        store.TryAcquire(Provider, Model, out _);
        store.ReportOutcome(Provider, Model, CircuitOutcome.CountedFailure);
        Assert.Equal(CircuitState.Closed, store.GetState(Provider, Model));
    }

    /// <summary>Checklist item 6 — a failed probe returns to OPEN with the next (doubled) cooldown step.</summary>
    [Fact]
    public void ReportOutcome_FailedProbe_ReturnsToOpen_DoublesCoooldown()
    {
        var time = new FakeTimeProvider();
        var store = new AiCircuitBreakerStore(time);
        OpenCircuit(store); // cooldown now 30s
        time.Advance(TimeSpan.FromSeconds(31));
        store.TryAcquire(Provider, Model, out _);

        var transitions = store.ReportOutcome(Provider, Model, CircuitOutcome.CountedFailure);

        Assert.Equal(CircuitState.Open, store.GetState(Provider, Model));
        var transition = Assert.Single(transitions);
        Assert.Equal(CircuitState.HalfOpen, transition.From);
        Assert.Equal(CircuitState.Open, transition.To);
        Assert.Equal(TimeSpan.FromSeconds(60), transition.CooldownDuration); // doubled from 30s

        // Still open immediately after (cooldown hasn't elapsed again).
        Assert.False(store.TryAcquire(Provider, Model, out _));
    }

    /// <summary>Design decision — a failed probe does NOT get the ordinary "1 more retry" treatment; it goes straight back to OPEN.</summary>
    [Fact]
    public void ReportOutcome_FailedProbe_DoesNotStayHalfOpenForAnotherAttempt()
    {
        var time = new FakeTimeProvider();
        var store = new AiCircuitBreakerStore(time);
        OpenCircuit(store);
        time.Advance(TimeSpan.FromSeconds(31));
        store.TryAcquire(Provider, Model, out _);
        store.ReportOutcome(Provider, Model, CircuitOutcome.CountedFailure);

        Assert.NotEqual(CircuitState.HalfOpen, store.GetState(Provider, Model));
    }

    /// <summary>Cooldown keeps doubling across repeated failed probes, capped at 10 minutes.</summary>
    [Fact]
    public void ReportOutcome_RepeatedFailedProbes_CooldownDoublesUpToTenMinuteCap()
    {
        var time = new FakeTimeProvider();
        var store = new AiCircuitBreakerStore(time);
        OpenCircuit(store); // 30s

        var expected = new[] { 60, 120, 240, 480, 600, 600 }; // seconds: doubles then caps at 600 (10 min)
        foreach (var expectedSeconds in expected)
        {
            time.Advance(TimeSpan.FromMinutes(11)); // always past whatever the current cooldown is
            store.TryAcquire(Provider, Model, out _);
            var transitions = store.ReportOutcome(Provider, Model, CircuitOutcome.CountedFailure);
            var transition = Assert.Single(transitions);
            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), transition.CooldownDuration);
        }
    }

    /// <summary>Checklist item 7 — a fresh store (simulating a restart) starts every circuit at CLOSED.</summary>
    [Fact]
    public void FreshStore_SimulatingRestart_StartsClosed()
    {
        var store = new AiCircuitBreakerStore(new FakeTimeProvider());
        Assert.Equal(CircuitState.Closed, store.GetState(Provider, Model));
        Assert.True(store.TryAcquire(Provider, Model, out _));
    }

    /// <summary>Circuits are independent per (Provider, ModelIdentifier) — opening one must not affect another, confirming the fallback-friendly design.</summary>
    [Fact]
    public void Circuits_AreIndependentPerModel()
    {
        var store = new AiCircuitBreakerStore(new FakeTimeProvider());
        OpenCircuit(store, model: "primary-model");

        Assert.Equal(CircuitState.Open, store.GetState(Provider, "primary-model"));
        Assert.Equal(CircuitState.Closed, store.GetState(Provider, "fallback-model"));
        Assert.True(store.TryAcquire(Provider, "fallback-model", out _));
    }

    private static void OpenCircuit(AiCircuitBreakerStore store, string provider = Provider, string model = Model)
    {
        for (var i = 0; i < 3; i++)
        {
            store.TryAcquire(provider, model, out _);
            store.ReportOutcome(provider, model, CircuitOutcome.CountedFailure);
        }
    }
}
