using Iemas.Application.Common.Interfaces;
using Iemas.Application.Escalations.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Escalations;

/// <summary>Read-side for the CMS/API — Escalation Event history per Case (§63/§89) and administrative listing, kept separate from the write-side EscalationService/EscalationPolicyService.</summary>
public class EscalationQueryService
{
    private readonly IAppDbContext _db;

    public EscalationQueryService(IAppDbContext db)
    {
        _db = db;
    }

    public async Task<List<EscalationEventDto>> GetForCaseAsync(Guid caseId, CancellationToken cancellationToken)
    {
        return await Project(_db.EscalationEvents.Where(e => e.CaseId == caseId).OrderByDescending(e => e.OccurredAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<List<EscalationEventDto>> SearchAsync(string? outcomeFilter, int take, CancellationToken cancellationToken)
    {
        var query = _db.EscalationEvents.AsQueryable();
        if (!string.IsNullOrWhiteSpace(outcomeFilter) && Enum.TryParse<Domain.Escalations.EscalationOutcome>(outcomeFilter, true, out var outcome))
        {
            query = query.Where(e => e.Outcome == outcome);
        }

        return await Project(query.OrderByDescending(e => e.OccurredAt).Take(take)).ToListAsync(cancellationToken);
    }

    private static IQueryable<EscalationEventDto> Project(IQueryable<Domain.Escalations.EscalationEvent> query) =>
        query.Select(e => new EscalationEventDto(
            e.Id, e.CaseId, e.Case.CaseNumber, e.EscalationPolicyId, e.EscalationPolicy != null ? e.EscalationPolicy.Name : null,
            e.Level, e.Trigger, e.RecipientType, e.RecipientDisplay, e.Channel, e.Outcome, e.SkipReason, e.Detail, e.OccurredAt));
}
