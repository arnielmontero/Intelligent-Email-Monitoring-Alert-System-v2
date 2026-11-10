using Iemas.Application.Common;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Operations;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Iemas.Domain.Escalations;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Inquiries;

public sealed record AgentInquiryCountsDto(
    Guid? EmployeeId, string AgentName,
    int AwaitingInitialResponse, int CustomerFollowUp, int AwaitingCustomer, int AwaitingInternal, int Inactive,
    int PotentiallyResolved, int Overdue, int Escalated, int OpenTotal, int ResolvedLast30Days);

public sealed record InquiryOverviewDto(AgentInquiryCountsDto Totals, IReadOnlyList<AgentInquiryCountsDto> Agents, DateTimeOffset GeneratedAt, int TotalDetected = 0);

public sealed record SetupCheckItemDto(string Key, string Label, bool Ok, string Detail, string Link);

public sealed record InquiryRowDto(
    Guid Id, string CaseNumber, string Subject, string Customer, string? AgentName, Guid? EmployeeId,
    string Stage, string StageLabel, DateTimeOffset StageChangedAt, string WorkStatus,
    DateTimeOffset FirstEmailReceivedAt, DateTimeOffset? FirstAgentResponseAt, DateTimeOffset LastActivityAt,
    DateTimeOffset? ResolutionDueAt, bool IsOverdue, int Escalations, string? AiVerdict, double? AiConfidence);

public sealed record ConversationMessageDto(Guid Id, string Role, string Direction, DateTimeOffset At, string From, string? FromName, string To, string? Cc, string Subject, string? Text, int Attachments);

public sealed record InquirySupervisionDto(
    Guid CaseId, string CaseNumber, string Stage, string StageLabel, DateTimeOffset StageChangedAt,
    DateTimeOffset? FirstAgentResponseAt, DateTimeOffset? LastCustomerMessageAt, DateTimeOffset? LastAgentMessageAt, DateTimeOffset? LastInternalActivityAt,
    DateTimeOffset? ResolutionDueAt, DateTimeOffset? OverdueSince, int EpisodeRemindersSent, int EpisodeEscalations,
    string? AiVerdict, double? AiConfidence, string? AiReason, DateTimeOffset? AiAssessedAt,
    IReadOnlyList<ConversationMessageDto> Conversation);

/// <summary>
/// Section 7 / Phase 8 - management visibility across all agents without opening any inbox: how many
/// inquiries each agent has in every stage, which are overdue or escalated, and each inquiry's full
/// conversation and supervision state.
/// </summary>
public class InquiryOverviewService
{
    private readonly IAppDbContext _db;
    private readonly AgentReplyProtector _protector;
    private readonly Iemas.Application.AiModels.IAiProviderConnectionResolver? _aiConnection;

    public InquiryOverviewService(IAppDbContext db, AgentReplyProtector protector, Iemas.Application.AiModels.IAiProviderConnectionResolver? aiConnection = null)
    {
        _db = db;
        _protector = protector;
        _aiConnection = aiConnection;
    }

    /// <summary>
    /// Is the system actually configured to supervise inquiries? Inspects real configuration (not
    /// defaults): API key, AI model, agent mailboxes, outbound SMTP, business calendar, escalation
    /// recipients and internal domains. Never returns a secret.
    /// </summary>
    public async Task<IReadOnlyList<SetupCheckItemDto>> GetSetupCheckAsync(CancellationToken cancellationToken)
    {
        var items = new List<SetupCheckItemDto>();
        void Add(string key, string label, bool ok, string detail, string link) => items.Add(new SetupCheckItemDto(key, label, ok, detail, link));

        var connection = _aiConnection is null ? null : await _aiConnection.ResolveAsync(cancellationToken);
        var hasKey = !string.IsNullOrWhiteSpace(connection?.ApiKey);
        Add("ai-key", "OpenRouter API key", hasKey,
            hasKey ? $"Configured ({connection!.KeySource}). Use Test on the AI Models page to check it works." : "No key. Uncertain emails cannot be classified and resolutions cannot be assessed.", "/ai-models");

        var models = await _db.AiModelConfigs.AsNoTracking().CountAsync(m => m.Enabled && m.TaskCapability == "EmailClassification", cancellationToken);
        Add("ai-model", "AI model", models > 0, models > 0 ? $"{models} enabled" : "No enabled model.", "/ai-models");

        var agents = await _db.EmailAccounts.AsNoTracking()
            .Where(a => a.Purpose == EmailAccountPurpose.Inbound && a.IsActive && a.MonitoringEnabled)
            .Select(a => new { a.EmailAddress, a.OwnerEmployeeId }).ToListAsync(cancellationToken);
        var unowned = agents.Count(a => a.OwnerEmployeeId is null);
        Add("agent-mailboxes", "Agent mailboxes monitored", agents.Count > 0 && unowned == 0,
            agents.Count == 0 ? "No active monitored mailbox."
            : unowned > 0 ? $"{unowned} of {agents.Count} mailbox(es) have no owner, so their inquiries have no agent."
            : $"{agents.Count} mailbox(es), all with an owner.", "/email-accounts");

        var smtp = await _db.EmailAccounts.AsNoTracking()
            .AnyAsync(a => a.Purpose == EmailAccountPurpose.Outbound && a.IsActive && a.Protocol == EmailProtocol.Smtp, cancellationToken);
        Add("outbound-smtp", "Outbound email for management escalations", smtp,
            smtp ? "An active SMTP account is configured." : "No active Outbound SMTP account: management cannot be emailed.", "/outbound-email");

        var calendar = await _db.ReminderPolicies.AsNoTracking().Include(p => p.Holidays)
            .FirstOrDefaultAsync(p => p.Enabled && p.IsDefault, cancellationToken);
        Add("calendar", "Business hours, working days and holidays", calendar is { RestrictToBusinessHours: true },
            calendar is null ? "No default Reminder Policy: deadlines count 08:00-17:00 Monday-Friday UTC."
            : !calendar.RestrictToBusinessHours ? "The default policy ignores business hours, so deadlines count around the clock."
            : $"{calendar.BusinessHoursStart:hh\\:mm}-{calendar.BusinessHoursEnd:hh\\:mm} {calendar.TimeZoneId}, {(calendar.ExcludeWeekends ? "weekdays only" : "7 days")}, {calendar.Holidays.Count} holiday(s).",
            "/reminder-policies");

        var policy = await _db.EscalationPolicies.AsNoTracking().Include(p => p.Levels)
            .FirstOrDefaultAsync(p => p.Enabled && p.IsDefault, cancellationToken);
        var level1 = policy?.Levels.OrderBy(l => l.Level).FirstOrDefault();
        var needsSupervisor = level1?.RecipientType == Domain.Escalations.EscalationRecipientType.EmployeeSupervisor;
        var ownerIds = agents.Where(a => a.OwnerEmployeeId != null).Select(a => a.OwnerEmployeeId!.Value).Distinct().ToList();
        var withoutSupervisor = needsSupervisor
            ? await _db.Employees.AsNoTracking().CountAsync(e => ownerIds.Contains(e.Id) && e.SupervisorEmployeeId == null, cancellationToken)
            : 0;
        Add("escalation", "Management escalation recipients", policy is not null && level1 is not null && withoutSupervisor == 0,
            policy is null ? "No default escalation policy."
            : level1 is null ? "The escalation policy has no levels."
            : withoutSupervisor > 0 ? $"{withoutSupervisor} agent(s) have no supervisor, so level 1 cannot reach anyone for them (Employees & Ownership)."
            : $"{policy.Levels.Count} level(s); level 1 goes to {level1.RecipientType}.", "/escalation-policies");

        var internalDomains = (await SystemSettingsService.GetValueAsync(_db, SystemSettingKeys.InternalDomains, cancellationToken))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Add("internal-domains", "Internal company domains", internalDomains.Length > 0,
            internalDomains.Length > 0 ? string.Join(", ", internalDomains) : "None: internal email could start customer inquiries.", "/system-settings");

        var enabled = await SystemSettingsService.GetBoolAsync(_db, SystemSettingKeys.SupervisionEnabled, cancellationToken);
        Add("supervision", "Inquiry supervision switched on", enabled,
            enabled ? "On" : "Off: the older reminder engines are used and stop at the first reply.", "/system-settings");
        return items;
    }

    public async Task<InquiryOverviewDto> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        var rows = await _db.Cases.AsNoTracking()
            .Where(c => (c.Stage != InquiryStage.Resolved && c.Stage != InquiryStage.Closed) || (c.Stage == InquiryStage.Resolved && c.StageChangedAt >= since))
            .Select(c => new
            {
                c.OwnerEmployeeId,
                Name = c.OwnerEmployee != null ? c.OwnerEmployee.FullName : null,
                c.Stage,
                Overdue = c.OverdueSince != null,
                Escalated = c.EpisodeEscalations > 0 || _db.EscalationEvents.Any(e => e.CaseId == c.Id && e.Outcome == EscalationOutcome.Executed),
            })
            .ToListAsync(cancellationToken);

        var typed = rows.Select(r => new CountRow(r.OwnerEmployeeId, r.Name, r.Stage, r.Overdue, r.Escalated)).ToList();
        var agents = typed.GroupBy(r => new { r.OwnerEmployeeId, r.Name })
            .Select(g => Count(g.Key.OwnerEmployeeId, g.Key.Name ?? "(unassigned)", g.ToList()))
            .OrderByDescending(a => a.Overdue + a.Escalated).ThenByDescending(a => a.OpenTotal).ThenBy(a => a.AgentName)
            .ToList();
        return new InquiryOverviewDto(Count(null, "All agents", typed), agents, DateTimeOffset.UtcNow, await _db.Cases.CountAsync(cancellationToken));
    }

    private sealed record CountRow(Guid? OwnerEmployeeId, string? Name, InquiryStage Stage, bool Overdue, bool Escalated);

    private static AgentInquiryCountsDto Count(Guid? id, string name, List<CountRow> list)
    {
        bool Open(CountRow r) => !InquiryLifecycle.IsTerminal(r.Stage);
        return new AgentInquiryCountsDto(id, name,
            list.Count(r => r.Stage == InquiryStage.AwaitingInitialResponse),
            list.Count(r => r.Stage == InquiryStage.CustomerFollowUpReceived),
            list.Count(r => InquiryLifecycle.CustomerOwesNextStep(r.Stage)),
            list.Count(r => r.Stage == InquiryStage.AwaitingInternal),
            list.Count(r => r.Stage == InquiryStage.Inactive),
            list.Count(r => r.Stage == InquiryStage.PotentiallyResolved),
            list.Count(r => Open(r) && r.Overdue),
            list.Count(r => Open(r) && r.Escalated),
            list.Count(Open),
            list.Count(r => r.Stage == InquiryStage.Resolved));
    }

    /// <summary>Bucket: awaiting-initial, follow-up, awaiting-customer, awaiting-internal, inactive, potentially-resolved, overdue, escalated, resolved, open (default).</summary>
    public async Task<PagedResult<InquiryRowDto>> ListAsync(string? bucket, Guid? employeeId, string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var q = _db.Cases.AsNoTracking();
        if (employeeId is not null) q = q.Where(c => c.OwnerEmployeeId == employeeId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            q = q.Where(c => c.CaseNumber.ToLower().Contains(term) || c.Subject.ToLower().Contains(term) || c.CustomerEmailAddress.ToLower().Contains(term));
        }

        var open = q.Where(c => c.Stage != InquiryStage.Resolved && c.Stage != InquiryStage.Closed);
        q = (bucket ?? "open").ToLowerInvariant() switch
        {
            "awaiting-initial" => q.Where(c => c.Stage == InquiryStage.AwaitingInitialResponse),
            "follow-up" => q.Where(c => c.Stage == InquiryStage.CustomerFollowUpReceived),
            "awaiting-customer" => q.Where(c => c.Stage == InquiryStage.InitialResponseReceived || c.Stage == InquiryStage.AgentFollowUpSent || c.Stage == InquiryStage.AwaitingCustomer),
            "awaiting-internal" => q.Where(c => c.Stage == InquiryStage.AwaitingInternal),
            "awaiting-agent" => q.Where(c => c.Stage == InquiryStage.AwaitingInitialResponse || c.Stage == InquiryStage.CustomerFollowUpReceived || c.Stage == InquiryStage.Inactive),
            "active" => q.Where(c => c.Stage == InquiryStage.InitialResponseReceived || c.Stage == InquiryStage.AgentFollowUpSent
                || c.Stage == InquiryStage.AwaitingCustomer || c.Stage == InquiryStage.AwaitingInternal),
            "inactive" => q.Where(c => c.Stage == InquiryStage.Inactive),
            "potentially-resolved" => q.Where(c => c.Stage == InquiryStage.PotentiallyResolved),
            "overdue" => open.Where(c => c.OverdueSince != null),
            "escalated" => open.Where(c => c.EpisodeEscalations > 0 || _db.EscalationEvents.Any(e => e.CaseId == c.Id && e.Outcome == EscalationOutcome.Executed)),
            "resolved" => q.Where(c => c.Stage == InquiryStage.Resolved || c.Stage == InquiryStage.Closed),
            "all" => q,
            _ => open,
        };

        var total = await q.CountAsync(cancellationToken);
        var rows = await q.OrderByDescending(c => c.OverdueSince != null).ThenBy(c => c.StageChangedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(c => new
            {
                c.Id, c.CaseNumber, c.Subject, c.CustomerEmailAddress, c.CustomerDisplayName,
                Agent = c.OwnerEmployee != null ? c.OwnerEmployee.FullName : null, c.OwnerEmployeeId,
                c.Stage, c.StageChangedAt, c.WorkStatus, c.FirstEmailReceivedAt, c.FirstAgentResponseAt, c.LastActivityAt,
                c.ResolutionDueAt, c.OverdueSince,
                Escalations = _db.EscalationEvents.Count(e => e.CaseId == c.Id && e.Outcome == EscalationOutcome.Executed),
                c.ResolutionVerdict, c.ResolutionConfidence,
            })
            .ToListAsync(cancellationToken);

        var items = rows.Select(r => new InquiryRowDto(r.Id, r.CaseNumber, r.Subject,
            string.IsNullOrWhiteSpace(r.CustomerDisplayName) ? r.CustomerEmailAddress : $"{r.CustomerDisplayName} <{r.CustomerEmailAddress}>",
            r.Agent, r.OwnerEmployeeId, r.Stage.ToString(), InquiryLifecycle.Label(r.Stage), r.StageChangedAt, r.WorkStatus.ToString(),
            r.FirstEmailReceivedAt, r.FirstAgentResponseAt, r.LastActivityAt, r.ResolutionDueAt, r.OverdueSince != null, r.Escalations,
            r.ResolutionVerdict?.ToString(), r.ResolutionConfidence)).ToList();
        return PagedResult<InquiryRowDto>.Create(items, total, page, pageSize);
    }

    /// <summary>The inquiry's supervision state and its whole conversation, both directions, in order. Agent reply text is decrypted only here.</summary>
    public async Task<InquirySupervisionDto?> GetSupervisionAsync(Guid caseId, CancellationToken cancellationToken)
    {
        var c = await _db.Cases.AsNoTracking().FirstOrDefaultAsync(x => x.Id == caseId, cancellationToken);
        if (c is null) return null;

        var messages = await _db.CaseEmails.AsNoTracking()
            .Where(ce => ce.CaseId == caseId)
            .OrderBy(ce => ce.EmailMessage.ReceivedAt)
            .Select(ce => new
            {
                ce.EmailMessageId, ce.Role, ce.EmailMessage.Direction, ce.EmailMessage.ReceivedAt, ce.EmailMessage.FromAddress, ce.EmailMessage.FromDisplayName, ce.EmailMessage.CcAddresses,
                ce.EmailMessage.ToAddresses, ce.EmailMessage.Subject, ce.EmailMessage.BodyText, ce.EmailMessage.BodyTextEncrypted, ce.EmailMessage.AttachmentCount,
            })
            .ToListAsync(cancellationToken);

        var conversation = messages.Select(m => new ConversationMessageDto(
            m.EmailMessageId, m.Role.ToString(), m.Direction.ToString(), m.ReceivedAt, m.FromAddress, m.FromDisplayName, m.ToAddresses, m.CcAddresses, m.Subject,
            m.Direction == MessageDirection.Outbound ? _protector.Reveal(m.BodyTextEncrypted) : m.BodyText,
            m.AttachmentCount)).ToList();

        return new InquirySupervisionDto(c.Id, c.CaseNumber, c.Stage.ToString(), InquiryLifecycle.Label(c.Stage), c.StageChangedAt,
            c.FirstAgentResponseAt, c.LastCustomerMessageAt, c.LastAgentMessageAt, c.LastInternalActivityAt,
            c.ResolutionDueAt, c.OverdueSince, c.EpisodeRemindersSent, c.EpisodeEscalations,
            c.ResolutionVerdict?.ToString(), c.ResolutionConfidence, c.ResolutionReason, c.ResolutionAssessedAt, conversation);
    }

    /// <summary>Instruction 14 / G7 - reassign an inquiry to another agent. History is kept; timers keep running (a handover does not reset a deadline), and the new owner is notified.</summary>
    public async Task<Result<bool>> ReassignAsync(Guid caseId, Guid newEmployeeId, string? actorDescription, CancellationToken cancellationToken)
    {
        var c = await _db.Cases.FirstOrDefaultAsync(x => x.Id == caseId, cancellationToken);
        if (c is null) return Result<bool>.Failure("Case not found.");
        if (InquiryLifecycle.IsTerminal(c.Stage)) return Result<bool>.Failure("This inquiry is closed; reopen it before reassigning.");
        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == newEmployeeId && e.IsActive, cancellationToken);
        if (employee is null) return Result<bool>.Failure("Employee not found or inactive.");
        if (c.OwnerEmployeeId == newEmployeeId) return Result<bool>.Failure("The inquiry is already assigned to this employee.");

        var previous = c.OwnerEmployeeId is Guid prevId
            ? await _db.Employees.AsNoTracking().Where(e => e.Id == prevId).Select(e => e.FullName).FirstOrDefaultAsync(cancellationToken)
            : null;
        c.OwnerEmployeeId = newEmployeeId;
        c.UpdatedAt = DateTimeOffset.UtcNow;
        _db.CaseEvents.Add(new CaseEvent
        {
            CaseId = c.Id,
            EventType = CaseEventType.Reassigned,
            Detail = $"Reassigned from {previous ?? "(unassigned)"} to {employee.FullName}{(actorDescription is null ? "" : $" by {actorDescription}")}. Supervision timers continue.",
        });
        await _db.SaveChangesAsync(cancellationToken);
        return Result<bool>.Success(true);
    }
}
