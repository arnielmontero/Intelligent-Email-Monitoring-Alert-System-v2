using Iemas.Domain.Common;

namespace Iemas.Domain.Reminders;

/// <summary>
/// Requirements §54 — configuration for the Reminder Engine. A single Case is governed by exactly
/// one applicable policy at any time, resolved by <see cref="ClassificationProfileId"/> when set,
/// falling back to <see cref="IsDefault"/> otherwise (mirrors the existing AiModelConfig
/// enabled/default/fallback pattern already used for classification model selection).
/// </summary>
public class ReminderPolicy : Entity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public bool IsDefault { get; set; }

    /// <summary>Optional — scope this policy to Cases whose email account uses a specific Classification Profile. Null = applies regardless (subject to IsDefault fallback still applying only when no more specific enabled policy matches).</summary>
    public Guid? ClassificationProfileId { get; set; }

    /// <summary>§54 "Initial delay" — time from a Case entering ActionRequired until the first reminder is due.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromHours(4);

    /// <summary>§54 "Reminder interval" — time between subsequent reminders while still unresolved.</summary>
    public TimeSpan ReminderInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>§54 "Maximum reminders" — hard ceiling; required so "No infinite reminder loops" (§54) is enforced structurally, not by convention.</summary>
    public int MaxReminders { get; set; } = 3;

    /// <summary>§54 "Minimum interval" — a floor beneath which two reminders (regardless of source — automatic or Remind-Later) may never be scheduled back-to-back for the same Case.</summary>
    public TimeSpan MinimumInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>§54 "Business hours" — reminders due outside this window are deferred to the next in-window moment, never sent early or silently dropped.</summary>
    public bool RestrictToBusinessHours { get; set; } = true;
    public TimeSpan BusinessHoursStart { get; set; } = TimeSpan.FromHours(8);
    public TimeSpan BusinessHoursEnd { get; set; } = TimeSpan.FromHours(17);

    /// <summary>§54 "Weekends" — Saturday/Sunday excluded when true.</summary>
    public bool ExcludeWeekends { get; set; } = true;

    /// <summary>§54 "Holidays" — dates (UTC, date-only) on which no reminder may fire; deferred like an out-of-business-hours moment.</summary>
    public ICollection<ReminderPolicyHoliday> Holidays { get; set; } = new List<ReminderPolicyHoliday>();

    /// <summary>§54 "Time zone" — business hours/weekend/holiday checks are evaluated in this IANA/Windows time zone id, not server-local or UTC wall-clock time.</summary>
    public string TimeZoneId { get; set; } = "UTC";

    /// <summary>§54 "Expiration" — a Scheduled reminder whose due time has passed this far beyond original scheduling is Expired rather than sent late, e.g. after an extended server outage.</summary>
    public TimeSpan? ExpirationWindow { get; set; } = TimeSpan.FromDays(3);

    /// <summary>§54 "Escalation threshold" — forward reference to Phase 9; recorded here as the reminder count at which this policy considers the Case eligible for escalation. No escalation logic is triggered by this phase; the field exists so the policy shape does not need revisiting when Phase 9 is built.</summary>
    public int? EscalationThresholdReminderCount { get; set; }
}

/// <summary>A single holiday date belonging to a <see cref="ReminderPolicy"/>. Modeled as its own row (not a delimited string) so it can be queried/validated normally.</summary>
public class ReminderPolicyHoliday : Entity
{
    public Guid ReminderPolicyId { get; set; }
    public ReminderPolicy ReminderPolicy { get; set; } = null!;

    /// <summary>Date-only, interpreted in the parent policy's TimeZoneId.</summary>
    public DateOnly Date { get; set; }
    public string? Label { get; set; }
}
