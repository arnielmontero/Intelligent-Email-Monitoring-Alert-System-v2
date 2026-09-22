using Iemas.Domain.Reminders;

namespace Iemas.Application.Reminders.Dtos;

public record ReminderPolicyDto(
    Guid Id,
    string Name,
    string? Description,
    bool Enabled,
    bool IsDefault,
    Guid? ClassificationProfileId,
    string? ClassificationProfileName,
    TimeSpan InitialDelay,
    TimeSpan ReminderInterval,
    int MaxReminders,
    TimeSpan MinimumInterval,
    bool RestrictToBusinessHours,
    TimeSpan BusinessHoursStart,
    TimeSpan BusinessHoursEnd,
    bool ExcludeWeekends,
    string TimeZoneId,
    TimeSpan? ExpirationWindow,
    int? EscalationThresholdReminderCount,
    IReadOnlyList<ReminderPolicyHolidayDto> Holidays);

public record ReminderPolicyHolidayDto(Guid Id, DateOnly Date, string? Label);

public record SaveReminderPolicyRequest(
    string Name,
    string? Description,
    bool Enabled,
    bool IsDefault,
    Guid? ClassificationProfileId,
    TimeSpan InitialDelay,
    TimeSpan ReminderInterval,
    int MaxReminders,
    TimeSpan MinimumInterval,
    bool RestrictToBusinessHours,
    TimeSpan BusinessHoursStart,
    TimeSpan BusinessHoursEnd,
    bool ExcludeWeekends,
    string TimeZoneId,
    TimeSpan? ExpirationWindow,
    int? EscalationThresholdReminderCount,
    IReadOnlyList<ReminderPolicyHolidayDto> Holidays);

public record ReminderDto(
    Guid Id,
    Guid CaseId,
    string CaseNumber,
    Guid? ReminderPolicyId,
    ReminderTrigger Trigger,
    ReminderStatus Status,
    int SequenceNumber,
    DateTimeOffset ScheduledForUtc,
    DateTimeOffset? RequestedForUtc,
    DateTimeOffset? ExecutedAtUtc,
    DateTimeOffset? CancelledAtUtc,
    ReminderCancelReason? CancelReason,
    string? CancelDetail,
    int DeliveryAttempts,
    string? LastFailureDetail);

public record ReminderRunResult(int Considered, int Sent, int Cancelled, int Failed, int Expired, int Rescheduled, long DurationMs);
