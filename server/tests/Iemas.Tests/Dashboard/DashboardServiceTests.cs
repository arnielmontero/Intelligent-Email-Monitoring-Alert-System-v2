using Iemas.Application.Dashboard;
using Iemas.Domain.Agents;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Iemas.Domain.Identity;
using Iemas.Tests.TestSupport;
using Xunit;

namespace Iemas.Tests.Dashboard;

public class DashboardServiceTests
{
    private static Employee CreateEmployee(string name = "John Smith") => new() { FullName = name, Email = $"{name.Replace(" ", ".").ToLowerInvariant()}@sawo.com" };

    private static Case CreateCase(Guid? ownerId, CaseWorkStatus workStatus, CaseReplyStatus replyStatus = CaseReplyStatus.AwaitingReply)
    {
        return new Case
        {
            CaseNumber = $"CASE-{Guid.NewGuid():N}"[..12],
            EmailAccountId = Guid.NewGuid(),
            CustomerEmailAddress = "customer@example.com",
            Subject = "Test",
            NormalizedSubject = "Test",
            OwnerEmployeeId = ownerId,
            WorkStatus = workStatus,
            ReplyStatus = replyStatus,
            FirstEmailReceivedAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>§87 — every listed metric comes back as a real, independently-verifiable count.</summary>
    [Fact]
    public async Task GetSummaryAsync_ComputesRealCounts_NotPlaceholders()
    {
        using var db = TestDbContext.CreateNew();
        var employee1 = CreateEmployee("Employee One");
        var employee2 = CreateEmployee("Employee Two");
        db.Employees.AddRange(employee1, employee2);
        await db.SaveChangesAsync();

        db.Cases.Add(CreateCase(employee1.Id, CaseWorkStatus.ActionRequired));
        db.Cases.Add(CreateCase(employee1.Id, CaseWorkStatus.Overdue));
        db.Cases.Add(CreateCase(employee2.Id, CaseWorkStatus.Escalated));
        db.Cases.Add(CreateCase(employee2.Id, CaseWorkStatus.Completed, CaseReplyStatus.Replied));
        db.Cases.Add(CreateCase(null, CaseWorkStatus.Cancelled));
        await db.SaveChangesAsync();

        // employee1 has a Connected Agent -> online; employee2 has none -> offline.
        db.Agents.Add(new Agent
        {
            ClientName = "Test PC",
            EnrollmentEmailAddress = employee1.Email,
            EmployeeId = employee1.Id,
            RegistrationStatus = AgentRegistrationStatus.Approved,
            ConnectionStatus = AgentConnectionStatus.Connected,
            RegistrationRequestToken = "tok-1",
        });
        db.Agents.Add(new Agent
        {
            ClientName = "Pending PC",
            EnrollmentEmailAddress = "someone@sawo.com",
            RegistrationStatus = AgentRegistrationStatus.Pending,
            RegistrationRequestToken = "tok-2",
        });
        await db.SaveChangesAsync();

        var service = new DashboardService(db);
        var summary = await service.GetSummaryAsync(CancellationToken.None);

        Assert.Equal(3, summary.OpenCases); // ActionRequired + Overdue + Escalated (Completed/Cancelled excluded)
        Assert.Equal(1, summary.Overdue);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(1, summary.OnlineEmployees);
        Assert.Equal(1, summary.OfflineEmployees);
        Assert.Equal(1, summary.PendingAgentApprovals);
        Assert.Equal(2, summary.OpenCasesByEmployee.Count);
        Assert.Contains(summary.OpenCasesByEmployee, x => x.EmployeeName == "Employee One" && x.OpenCaseCount == 2);
        Assert.Contains(summary.OpenCasesByEmployee, x => x.EmployeeName == "Employee Two" && x.OpenCaseCount == 1);
    }

    [Fact]
    public async Task GetSummaryAsync_NoData_ReturnsZeroesNotErrors()
    {
        using var db = TestDbContext.CreateNew();
        var service = new DashboardService(db);

        var summary = await service.GetSummaryAsync(CancellationToken.None);

        Assert.Equal(0, summary.OpenCases);
        Assert.Equal(0, summary.OnlineEmployees);
        Assert.Empty(summary.OpenCasesByEmployee);
    }
}
