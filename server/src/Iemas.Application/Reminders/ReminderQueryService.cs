using Iemas.Application.Common.Interfaces;
using Iemas.Application.Reminders.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Reminders;

/// <summary>Read-side for the CMS — Reminder history per Case (§89 investigation trail) and administrative listing, kept separate from the write-side scheduling/execution services.</summary>
public class ReminderQueryService
{
    private readonly IAppDbContext _db;

    public ReminderQueryService(IAppDbContext db)
    {
        _db = db;
    }

    public async Task<List<ReminderDto>> GetForCaseAsync(Guid caseId, CancellationToken cancellationToken)
    {
        return await _db.Reminders
            .Where(r => r.CaseId == caseId)
            .OrderByDescending(r => r.SequenceNumber)
            .Select(r => new ReminderDto(
                r.Id, r.CaseId, r.Case.CaseNumber, r.ReminderPolicyId, r.Trigger, r.Status, r.SequenceNumber,
                r.ScheduledForUtc, r.RequestedForUtc, r.ExecutedAtUtc, r.CancelledAtUtc, r.CancelReason, r.CancelDetail,
                r.DeliveryAttempts, r.LastFailureDetail))
            .ToListAsync(cancellationToken);
    }

    public async Task<List<ReminderDto>> SearchAsync(string? statusFilter, int take, CancellationToken cancellationToken)
    {
        var query = _db.Reminders.AsQueryable();
        if (!string.IsNullOrWhiteSpace(statusFilter) && Enum.TryParse<Domain.Reminders.ReminderStatus>(statusFilter, true, out var status))
        {
            query = query.Where(r => r.Status == status);
        }

        return await query
            .OrderByDescending(r => r.ScheduledForUtc)
            .Take(take)
            .Select(r => new ReminderDto(
                r.Id, r.CaseId, r.Case.CaseNumber, r.ReminderPolicyId, r.Trigger, r.Status, r.SequenceNumber,
                r.ScheduledForUtc, r.RequestedForUtc, r.ExecutedAtUtc, r.CancelledAtUtc, r.CancelReason, r.CancelDetail,
                r.DeliveryAttempts, r.LastFailureDetail))
            .ToListAsync(cancellationToken);
    }
}
