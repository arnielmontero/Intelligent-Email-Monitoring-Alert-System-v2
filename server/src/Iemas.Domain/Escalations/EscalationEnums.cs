namespace Iemas.Domain.Escalations;

/// <summary>Requirements §59 — the five supported recipient types, resolved from organizational data where possible.</summary>
public enum EscalationRecipientType
{
    Employee = 0,
    EmployeeSupervisor = 1,
    DepartmentManager = 2,
    SpecificEmployee = 3,
    SpecificGroup = 4,
}

/// <summary>
/// Requirements §60/§63 — the outcome of one escalation attempt for one Case at one level. This is
/// the escalation *decision/state* this phase owns — never a real email-send result (§62's
/// QUEUED/SENDING/SENT/DELIVERY_CONFIRMED/SEND_FAILED/RETRY/FAILED lifecycle is delivery-layer
/// territory, explicitly out of scope per this phase's instructions). "Executed" here means "the
/// escalation was decided and recorded as due for delivery," not "an email left this server."
/// </summary>
public enum EscalationOutcome
{
    Executed = 0,
    Skipped = 1,
    RecipientUnresolved = 2,
    Failed = 3,
}

/// <summary>Requirements §60 — why an escalation attempt was skipped rather than executed, recorded for investigation (§89), same discipline as ReminderCancelReason in Phase 8.</summary>
public enum EscalationSkipReason
{
    /// <summary>§60.1 "Case still exists" — defensive; should not occur given Restrict delete behavior.</summary>
    CaseNotFound = 0,

    /// <summary>§60.2/§60.6/§60.7 "Case is active" / "not completed" / "not cancelled."</summary>
    CaseNotActive = 1,

    /// <summary>§60.3/§60.4/§60.5 "Required work is still incomplete" / "reply still required" / "no verified reply has resolved the reply requirement" — the reply was verified, so escalation is no longer warranted.</summary>
    ReplyVerified = 2,

    /// <summary>§60.8 "Escalation level has not already executed" — idempotency guard, not a real duplicate attempt.</summary>
    LevelAlreadyExecuted = 3,

    /// <summary>§60.9 "Policy is still enabled."</summary>
    PolicyDisabled = 4,

    /// <summary>Reminder→Escalation threshold (Phase 8's ReminderPolicy.EscalationThresholdReminderCount) not yet reached.</summary>
    ThresholdNotReached = 5,

    /// <summary>Cooldown (§57 policy field) since the last escalation for this Case has not yet elapsed.</summary>
    CooldownActive = 6,

    /// <summary>Maximum Level (§57 policy field) already reached — no further level exists to escalate to.</summary>
    MaximumLevelReached = 7,

    /// <summary>No enabled Escalation Policy applies to this Case at all (no matching Classification Profile/Category/Priority, and no default).</summary>
    NoApplicablePolicy = 8,
}
