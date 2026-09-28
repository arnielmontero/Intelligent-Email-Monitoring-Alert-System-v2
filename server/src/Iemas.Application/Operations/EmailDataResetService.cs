using System.Text.RegularExpressions;
using Iemas.Application.Common.Interfaces;
using Iemas.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Operations;

/// <summary>How many records of each kind a reset removes (or removed).</summary>
public record EmailDataResetCountsDto(
    int Emails,
    int Cases,
    int CaseEvents,
    int AiDecisions,
    int ReplyChecks,
    int Reminders,
    int Escalations,
    int Notifications,
    int EmployeeActions,
    int IntakeLogs)
{
    public int Total => Emails + Cases + CaseEvents + AiDecisions + ReplyChecks + Reminders + Escalations + Notifications + EmployeeActions + IntakeLogs;
}

/// <summary>A configuration record that looks like leftover test data (test employee, test mailbox, …).</summary>
public record TestRecordDto(string Kind, Guid Id, string Name, string Detail);

public record EmailDataResetPreviewDto(EmailDataResetCountsDto Counts, List<TestRecordDto> TestRecords);

public record EmailDataResetResultDto(EmailDataResetCountsDto Removed, DateTimeOffset NewCutoff, List<TestRecordDto> RemovedTestRecords);

/// <summary>
/// Removes everything that came from email — emails, AI decisions, Cases and their history, reply checks,
/// reminders, escalations, pop-ups and employee actions — and keeps all configuration (users, employees,
/// mailboxes and passwords, agents, AI setup, policies, templates, settings), the AI cost history and the
/// audit log. Every mailbox's cut-off moves to the reset time and the fetch position is kept, so nothing
/// already in the mailboxes is downloaded or classified again: IEMAS starts fresh from now.
/// Optionally it also removes leftover test records (test employees, mailboxes, agents, users, departments)
/// that the person ticked; policies and escalation groups are never removed.
/// </summary>
public class EmailDataResetService
{
    /// <summary>What the person must type to confirm, so a reset never happens by a stray click.</summary>
    public const string ConfirmationWord = "RESET";

    private const int BatchSize = 500;

    private static readonly string[] TestDomains = { "iemas.local", "localhost", "example.com", "example.org", "test.local" };

    /// <summary>Addresses like "admin2.66e6af@…" that automated verification runs generate.</summary>
    private static readonly Regex GeneratedTestAddress = new(@"\.[0-9a-f]{6}@", RegexOptions.Compiled);

    private readonly IAppDbContext _db;
    private readonly IAuditService _audit;
    private readonly ICurrentUserService _currentUser;

    public EmailDataResetService(IAppDbContext db, IAuditService audit, ICurrentUserService currentUser)
    {
        _db = db;
        _audit = audit;
        _currentUser = currentUser;
    }

    public async Task<EmailDataResetPreviewDto> PreviewAsync(CancellationToken cancellationToken) =>
        new(await CountAsync(cancellationToken), await DetectTestRecordsAsync(cancellationToken));

    private async Task<EmailDataResetCountsDto> CountAsync(CancellationToken cancellationToken) => new(
        await _db.EmailMessages.CountAsync(cancellationToken),
        await _db.Cases.CountAsync(cancellationToken),
        await _db.CaseEvents.CountAsync(cancellationToken),
        await _db.EmailClassifications.CountAsync(cancellationToken) + await _db.AiClassificationLogs.CountAsync(cancellationToken),
        await _db.ReplyVerificationAttempts.CountAsync(cancellationToken),
        await _db.Reminders.CountAsync(cancellationToken),
        await _db.EscalationEvents.CountAsync(cancellationToken),
        await _db.Notifications.CountAsync(cancellationToken),
        await _db.AgentCaseActions.CountAsync(cancellationToken),
        await _db.EmailIntakeLogs.CountAsync(cancellationToken));

    public async Task<EmailDataResetResultDto?> ResetAsync(
        string? confirmation, IReadOnlyCollection<Guid>? removeTestRecordIds, CancellationToken cancellationToken)
    {
        if (!string.Equals(confirmation?.Trim(), ConfirmationWord, StringComparison.Ordinal)) return null;

        // Move the cut-off first: anything fetched while the reset runs is newer than it and is kept as new work.
        var cutoff = DateTimeOffset.UtcNow;
        foreach (var account in await _db.EmailAccounts.ToListAsync(cancellationToken))
        {
            account.ProcessEmailsReceivedAfter = cutoff;
            account.UpdatedAt = cutoff;
        }
        await _db.SaveChangesAsync(cancellationToken);

        // Children before parents: reminders point at employee actions, actions at Case events,
        // emails at Cases. Each step deletes everything present, so a reset that fails half-way can simply be run again.
        var reminders = await DeleteAllAsync(_db.Reminders, cancellationToken);
        var escalations = await DeleteAllAsync(_db.EscalationEvents, cancellationToken);
        var notifications = await DeleteAllAsync(_db.Notifications, cancellationToken);
        var actions = await DeleteAllAsync(_db.AgentCaseActions, cancellationToken);
        var replyChecks = await DeleteAllAsync(_db.ReplyVerificationAttempts, cancellationToken);
        await DeleteAllAsync(_db.CaseEmails, cancellationToken);

        // AI cost history is kept (it is money already spent); only its link to the deleted email goes.
        foreach (var usage in await _db.AiUsageRecords.Where(u => u.EmailMessageId != null).ToListAsync(cancellationToken))
            usage.EmailMessageId = null;
        await _db.SaveChangesAsync(cancellationToken);

        var aiDecisions = await DeleteAllAsync(_db.AiClassificationLogs, cancellationToken)
            + await DeleteAllAsync(_db.EmailClassifications, cancellationToken);
        await DeleteAllAsync(_db.EmailAttachmentMetadata, cancellationToken);
        var emails = await DeleteAllAsync(_db.EmailMessages, cancellationToken);
        var caseEvents = await DeleteAllAsync(_db.CaseEvents, cancellationToken);
        var cases = await DeleteAllAsync(_db.Cases, cancellationToken);
        var intakeLogs = await DeleteAllAsync(_db.EmailIntakeLogs, cancellationToken);

        var removed = new EmailDataResetCountsDto(emails, cases, caseEvents, aiDecisions, replyChecks, reminders, escalations,
            notifications, actions, intakeLogs);

        // Only records that are still detected as test data can be removed, whatever ids the request carries.
        var requested = removeTestRecordIds?.ToHashSet() ?? new HashSet<Guid>();
        var removedTestRecords = requested.Count == 0
            ? new List<TestRecordDto>()
            : await RemoveTestRecordsAsync((await DetectTestRecordsAsync(cancellationToken)).Where(r => requested.Contains(r.Id)).ToList(), cancellationToken);

        await _audit.LogAsync("EMAIL_DATA_RESET", "System", null,
            $"Removed {removed.Emails} emails, {removed.Cases} Cases, {removed.CaseEvents} Case events, {removed.AiDecisions} AI decisions, " +
            $"{removed.ReplyChecks} reply checks, {removed.Reminders} reminders, {removed.Escalations} escalations, " +
            $"{removed.Notifications} notifications, {removed.EmployeeActions} employee actions, {removed.IntakeLogs} intake logs. " +
            $"Mailbox cut-off moved to {cutoff:u}; configuration kept." +
            (removedTestRecords.Count == 0 ? "" : $" Test records removed: {string.Join(", ", removedTestRecords.Select(r => $"{r.Kind} {r.Name}"))}."),
            cancellationToken);

        return new EmailDataResetResultDto(removed, cutoff, removedTestRecords);
    }

    private static bool IsTestEmail(string? email)
    {
        var at = email?.LastIndexOf('@') ?? -1;
        if (at < 0) return false;
        var domain = email![(at + 1)..].Trim().ToLowerInvariant();
        return TestDomains.Any(t => domain == t || domain.EndsWith("." + t, StringComparison.Ordinal));
    }

    private static bool IsTestName(string? name) =>
        name is not null && (name.StartsWith("Live Verification", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("e2e-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Sample", StringComparison.OrdinalIgnoreCase));

    /// <summary>Test employees, mailboxes, agents, users and departments left over from testing. Policies are never listed.</summary>
    public async Task<List<TestRecordDto>> DetectTestRecordsAsync(CancellationToken cancellationToken)
    {
        var result = new List<TestRecordDto>();

        var employees = (await _db.Employees.AsNoTracking().ToListAsync(cancellationToken))
            .Where(e => IsTestEmail(e.Email) || IsTestName(e.FullName)).ToList();
        var employeeIds = employees.Select(e => e.Id).ToHashSet();
        result.AddRange(employees.Select(e => new TestRecordDto("Employee", e.Id, e.FullName, e.Email)));

        var mailboxes = (await _db.EmailAccounts.AsNoTracking().ToListAsync(cancellationToken))
            .Where(a => IsTestEmail(a.EmailAddress) || a.Host.Contains("greenmail", StringComparison.OrdinalIgnoreCase)
                || a.Host.Equals(SampleDataService.SampleMailboxHost, StringComparison.OrdinalIgnoreCase)
                || a.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var mailboxAddresses = mailboxes.Select(a => a.EmailAddress.ToLowerInvariant()).ToHashSet();
        result.AddRange(mailboxes.Select(a => new TestRecordDto("Mailbox", a.Id, a.EmailAddress, $"server {a.Host}{(a.IsActive ? "" : ", deactivated")}")));

        var agents = (await _db.Agents.AsNoTracking().ToListAsync(cancellationToken))
            .Where(a => IsTestName(a.ClientName) || IsTestEmail(a.EnrollmentEmailAddress)
                || mailboxAddresses.Contains(a.EnrollmentEmailAddress.ToLowerInvariant()) || (a.EmployeeId != null && employeeIds.Contains(a.EmployeeId.Value)))
            .ToList();
        result.AddRange(agents.Select(a => new TestRecordDto("Windows Agent", a.Id, a.ClientName, $"{a.EnrollmentEmailAddress}, {a.RegistrationStatus}")));

        var currentUserId = _currentUser.UserId;
        var users = (await _db.Users.AsNoTracking().ToListAsync(cancellationToken))
            .Where(u => u.Id != currentUserId && (IsTestEmail(u.Email) || (!u.IsActive && GeneratedTestAddress.IsMatch(u.Email))))
            .ToList();
        result.AddRange(users.Select(u => new TestRecordDto("CMS user", u.Id, u.Email, u.IsActive ? "active" : "inactive")));

        var departments = (await _db.Departments.AsNoTracking().ToListAsync(cancellationToken)).Where(d => IsTestName(d.Name)).ToList();
        result.AddRange(departments.Select(d => new TestRecordDto("Department", d.Id, d.Name, "")));

        return result;
    }

    /// <summary>
    /// Removes the chosen test records. Links from kept records (a real employee's supervisor, a department's manager,
    /// a mailbox owner, an escalation level's specific person, a group membership) are cleared rather than blocking.
    /// </summary>
    private async Task<List<TestRecordDto>> RemoveTestRecordsAsync(List<TestRecordDto> records, CancellationToken cancellationToken)
    {
        HashSet<Guid> Ids(string kind) => records.Where(r => r.Kind == kind).Select(r => r.Id).ToHashSet();
        var agentIds = Ids("Windows Agent");
        var mailboxIds = Ids("Mailbox");
        var userIds = Ids("CMS user");
        var employeeIds = Ids("Employee");
        var departmentIds = Ids("Department");

        // An employee still used by an Agent that stays can't go; keep both and report only what was removed.
        var keptAgentOwners = await _db.Agents.Where(a => !agentIds.Contains(a.Id) && a.EmployeeId != null).Select(a => a.EmployeeId!.Value).ToListAsync(cancellationToken);
        employeeIds.ExceptWith(keptAgentOwners);

        _db.AgentCredentials.RemoveRange(await _db.AgentCredentials.Where(c => agentIds.Contains(c.AgentId)).ToListAsync(cancellationToken));
        _db.AgentLogs.RemoveRange(await _db.AgentLogs.Where(l => agentIds.Contains(l.AgentId)).ToListAsync(cancellationToken));
        _db.Agents.RemoveRange(await _db.Agents.Where(a => agentIds.Contains(a.Id)).ToListAsync(cancellationToken));
        await _db.SaveChangesAsync(cancellationToken);

        _db.EmailCredentials.RemoveRange(await _db.EmailCredentials.Where(c => mailboxIds.Contains(c.EmailAccountId)).ToListAsync(cancellationToken));
        _db.EmailSyncStates.RemoveRange(await _db.EmailSyncStates.Where(x => mailboxIds.Contains(x.EmailAccountId)).ToListAsync(cancellationToken));
        _db.EmailIntakeLogs.RemoveRange(await _db.EmailIntakeLogs.Where(x => mailboxIds.Contains(x.EmailAccountId)).ToListAsync(cancellationToken));
        _db.EmailAccounts.RemoveRange(await _db.EmailAccounts.Where(a => mailboxIds.Contains(a.Id)).ToListAsync(cancellationToken));
        await _db.SaveChangesAsync(cancellationToken);

        foreach (var agent in await _db.Agents.Where(a => a.ApprovedByUserId != null && userIds.Contains(a.ApprovedByUserId.Value)).ToListAsync(cancellationToken))
            agent.ApprovedByUserId = null;
        _db.RefreshTokens.RemoveRange(await _db.RefreshTokens.Where(t => userIds.Contains(t.UserId)).ToListAsync(cancellationToken));
        _db.UserRoles.RemoveRange(await _db.UserRoles.Where(r => userIds.Contains(r.UserId)).ToListAsync(cancellationToken));
        _db.Users.RemoveRange(await _db.Users.Where(u => userIds.Contains(u.Id)).ToListAsync(cancellationToken));
        await _db.SaveChangesAsync(cancellationToken);

        foreach (var e in await _db.Employees.Where(e => e.SupervisorEmployeeId != null && employeeIds.Contains(e.SupervisorEmployeeId.Value)).ToListAsync(cancellationToken))
            e.SupervisorEmployeeId = null;
        foreach (var d in await _db.Departments.Where(d => d.ManagerEmployeeId != null && employeeIds.Contains(d.ManagerEmployeeId.Value)).ToListAsync(cancellationToken))
            d.ManagerEmployeeId = null;
        foreach (var a in await _db.EmailAccounts.Where(a => a.OwnerEmployeeId != null && employeeIds.Contains(a.OwnerEmployeeId.Value)).ToListAsync(cancellationToken))
            a.OwnerEmployeeId = null;
        foreach (var l in await _db.EscalationLevels.Where(l => l.SpecificEmployeeId != null && employeeIds.Contains(l.SpecificEmployeeId.Value)).ToListAsync(cancellationToken))
            l.SpecificEmployeeId = null;
        foreach (var u in await _db.Users.Where(u => u.EmployeeId != null && employeeIds.Contains(u.EmployeeId.Value)).ToListAsync(cancellationToken))
            u.EmployeeId = null;
        _db.EscalationGroupMembers.RemoveRange(await _db.EscalationGroupMembers.Where(m => employeeIds.Contains(m.EmployeeId)).ToListAsync(cancellationToken));
        _db.Employees.RemoveRange(await _db.Employees.Where(e => employeeIds.Contains(e.Id)).ToListAsync(cancellationToken));
        await _db.SaveChangesAsync(cancellationToken);

        foreach (var e in await _db.Employees.Where(e => e.DepartmentId != null && departmentIds.Contains(e.DepartmentId.Value)).ToListAsync(cancellationToken))
            e.DepartmentId = null;
        _db.Departments.RemoveRange(await _db.Departments.Where(d => departmentIds.Contains(d.Id)).ToListAsync(cancellationToken));
        await _db.SaveChangesAsync(cancellationToken);

        return records.Where(r => r.Kind != "Employee" || employeeIds.Contains(r.Id)).ToList();
    }

    /// <summary>Deletes by id in batches, without loading full rows (email bodies can be large).</summary>
    private async Task<int> DeleteAllAsync<T>(DbSet<T> set, CancellationToken cancellationToken) where T : Entity, new()
    {
        var total = 0;
        while (true)
        {
            var ids = await set.Select(e => e.Id).Take(BatchSize).ToListAsync(cancellationToken);
            if (ids.Count == 0) return total;
            foreach (var id in ids)
                set.Remove(set.Local.FirstOrDefault(e => e.Id == id) ?? new T { Id = id });
            await _db.SaveChangesAsync(cancellationToken);
            total += ids.Count;
        }
    }
}
