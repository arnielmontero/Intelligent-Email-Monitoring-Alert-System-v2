using Iemas.Domain.Common;

namespace Iemas.Domain.Escalations;

/// <summary>
/// Requirements §57 — configuration for the Escalation Engine. Mirrors Phase 8's ReminderPolicy
/// shape/resolution precedent deliberately (profile-scoped policies take precedence over a single
/// default, same as ReminderPolicy/AiModelConfig before it) rather than inventing a new resolution
/// strategy for this phase.
/// </summary>
public class EscalationPolicy : Entity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public bool IsDefault { get; set; }

    /// <summary>§57 "Classification Profile" — optional scope; null + IsDefault = applies when no more specific policy matches.</summary>
    public Guid? ClassificationProfileId { get; set; }

    /// <summary>§57 "Categories" — comma-separated category names this policy applies to; empty/null = any category.</summary>
    public string? Categories { get; set; }

    /// <summary>§57 "Priority" — optional; null = any priority.</summary>
    public Ai.ClassificationPriority? Priority { get; set; }

    /// <summary>§57 "Trigger" — the Reminder sequence number (Phase 8's ReminderPolicy.EscalationThresholdReminderCount concept, now actually acted on) at/after which Level 1 becomes eligible. Independent of ReminderPolicy — an Escalation Policy defines its own trigger threshold rather than reading it off whichever Reminder Policy happened to apply.</summary>
    public int TriggerReminderCount { get; set; } = 3;

    /// <summary>§57 "Grace Period" — minimum time a Case must have been outstanding (since FirstEmailReceivedAt) before Level 1 is eligible, independent of the reminder count trigger. Both conditions must hold.</summary>
    public TimeSpan GracePeriod { get; set; } = TimeSpan.FromDays(2);

    /// <summary>§57 "Cooldown" — minimum time between two escalation levels firing for the same Case, distinct from each Level's own "after N days" offset (§58) which is measured from the previous level, not from Case creation.</summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromDays(1);

    /// <summary>§57/§58 "Maximum Level" — V1 supports at most 3 (§58).</summary>
    public int MaximumLevel { get; set; } = 3;

    /// <summary>§57 "Channel" — the delivery channel a later phase will use; recorded here as data the escalation decision carries, not acted upon by this phase.</summary>
    public string Channel { get; set; } = "Email";

    public ICollection<EscalationLevel> Levels { get; set; } = new List<EscalationLevel>();
}

/// <summary>
/// Requirements §58 — one level within a policy's chain. "After N days" is measured from the
/// previous level's execution (or from the policy's GracePeriod-eligible moment, for Level 1) —
/// each level's own Delay field, not a single global offset, so the §58 example (2 days, then +1,
/// then +1) is representable exactly.
/// </summary>
public class EscalationLevel : Entity
{
    public Guid EscalationPolicyId { get; set; }
    public EscalationPolicy EscalationPolicy { get; set; } = null!;

    /// <summary>1-based — §58 "Level 1"/"Level 2"/"Level 3."</summary>
    public int Level { get; set; }

    /// <summary>Delay after the previous level (or after Grace Period, for Level 1) before this level becomes eligible.</summary>
    public TimeSpan DelayAfterPreviousLevel { get; set; }

    public EscalationRecipientType RecipientType { get; set; }

    /// <summary>Only meaningful when RecipientType is SpecificEmployee.</summary>
    public Guid? SpecificEmployeeId { get; set; }
    public Identity.Employee? SpecificEmployee { get; set; }

    /// <summary>Only meaningful when RecipientType is SpecificGroup.</summary>
    public Guid? SpecificGroupId { get; set; }
    public EscalationGroup? SpecificGroup { get; set; }
}

/// <summary>
/// Requirements §59 "Specific Group" — no broader group/permissions concept exists elsewhere in
/// this codebase; modeled here as the minimal named list of Employees this recipient type needs,
/// scoped to escalation only, not a general-purpose org-group system.
/// </summary>
public class EscalationGroup : Entity
{
    public string Name { get; set; } = string.Empty;
    public ICollection<EscalationGroupMember> Members { get; set; } = new List<EscalationGroupMember>();
}

public class EscalationGroupMember : Entity
{
    public Guid EscalationGroupId { get; set; }
    public EscalationGroup EscalationGroup { get; set; } = null!;

    public Guid EmployeeId { get; set; }
    public Identity.Employee Employee { get; set; } = null!;
}
