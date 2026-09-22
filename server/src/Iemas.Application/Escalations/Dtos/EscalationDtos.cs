using Iemas.Domain.Ai;
using Iemas.Domain.Escalations;

namespace Iemas.Application.Escalations.Dtos;

public record EscalationLevelDto(
    Guid Id,
    int Level,
    TimeSpan DelayAfterPreviousLevel,
    EscalationRecipientType RecipientType,
    Guid? SpecificEmployeeId,
    string? SpecificEmployeeName,
    Guid? SpecificGroupId,
    string? SpecificGroupName);

public record SaveEscalationLevelRequest(
    int Level,
    TimeSpan DelayAfterPreviousLevel,
    EscalationRecipientType RecipientType,
    Guid? SpecificEmployeeId,
    Guid? SpecificGroupId);

public record EscalationPolicyDto(
    Guid Id,
    string Name,
    string? Description,
    bool Enabled,
    bool IsDefault,
    Guid? ClassificationProfileId,
    string? ClassificationProfileName,
    string? Categories,
    ClassificationPriority? Priority,
    int TriggerReminderCount,
    TimeSpan GracePeriod,
    TimeSpan Cooldown,
    int MaximumLevel,
    string Channel,
    IReadOnlyList<EscalationLevelDto> Levels);

public record SaveEscalationPolicyRequest(
    string Name,
    string? Description,
    bool Enabled,
    bool IsDefault,
    Guid? ClassificationProfileId,
    string? Categories,
    ClassificationPriority? Priority,
    int TriggerReminderCount,
    TimeSpan GracePeriod,
    TimeSpan Cooldown,
    int MaximumLevel,
    string Channel,
    IReadOnlyList<SaveEscalationLevelRequest> Levels);

public record EscalationGroupDto(Guid Id, string Name, IReadOnlyList<EscalationGroupMemberDto> Members);
public record EscalationGroupMemberDto(Guid EmployeeId, string EmployeeName);
public record SaveEscalationGroupRequest(string Name, IReadOnlyList<Guid> EmployeeIds);

public record EscalationEventDto(
    Guid Id,
    Guid CaseId,
    string CaseNumber,
    Guid? EscalationPolicyId,
    string? EscalationPolicyName,
    int Level,
    string Trigger,
    EscalationRecipientType? RecipientType,
    string? RecipientDisplay,
    string? Channel,
    EscalationOutcome Outcome,
    EscalationSkipReason? SkipReason,
    string? Detail,
    DateTimeOffset OccurredAt);

public record EscalationRunResult(int Considered, int Executed, int Skipped, int RecipientUnresolved, int Failed, long DurationMs);

public record TestEscalationPolicyResult(bool WouldEscalate, int? EligibleLevel, string? RecipientDisplay, string Reason);
