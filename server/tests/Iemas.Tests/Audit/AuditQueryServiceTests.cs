using Iemas.Application.Audit;
using Iemas.Domain.Audit;
using Iemas.Tests.TestSupport;
using Xunit;

namespace Iemas.Tests.Audit;

public class AuditQueryServiceTests
{
    private static AuditLog CreateLog(string action, string entityType, string? entityId = null, DateTimeOffset? occurredAt = null)
    {
        var log = new AuditLog
        {
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            UserEmail = "admin@sawo.com",
        };
        if (occurredAt is DateTimeOffset ts)
        {
            log.CreatedAt = ts;
        }
        return log;
    }

    /// <summary>§67 — results come back newest first, matching every other history/log view in this system.</summary>
    [Fact]
    public async Task SearchAsync_NoFilters_ReturnsNewestFirst()
    {
        using var db = TestDbContext.CreateNew();
        db.AuditLogs.Add(CreateLog("AGENT_APPROVED", "Agent", occurredAt: DateTimeOffset.UtcNow.AddHours(-2)));
        db.AuditLogs.Add(CreateLog("AGENT_REVOKED", "Agent", occurredAt: DateTimeOffset.UtcNow.AddHours(-1)));
        db.AuditLogs.Add(CreateLog("AGENT_CREDENTIAL_ROTATED", "Agent", occurredAt: DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        var service = new AuditQueryService(db);
        var results = await service.SearchAsync(null, null, null, null, 100, CancellationToken.None);

        Assert.Equal(3, results.Count);
        Assert.Equal("AGENT_CREDENTIAL_ROTATED", results[0].Action);
        Assert.Equal("AGENT_REVOKED", results[1].Action);
        Assert.Equal("AGENT_APPROVED", results[2].Action);
    }

    [Fact]
    public async Task SearchAsync_FilterByAction_ReturnsOnlyMatching()
    {
        using var db = TestDbContext.CreateNew();
        db.AuditLogs.Add(CreateLog("AGENT_APPROVED", "Agent"));
        db.AuditLogs.Add(CreateLog("AGENT_REJECTED", "Agent"));
        db.AuditLogs.Add(CreateLog("EMPLOYEE_CREATED", "Employee"));
        await db.SaveChangesAsync();

        var service = new AuditQueryService(db);
        var results = await service.SearchAsync("AGENT_", null, null, null, 100, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.StartsWith("AGENT_", r.Action));
    }

    [Fact]
    public async Task SearchAsync_FilterByEntityType_ReturnsOnlyMatching()
    {
        using var db = TestDbContext.CreateNew();
        db.AuditLogs.Add(CreateLog("AGENT_APPROVED", "Agent"));
        db.AuditLogs.Add(CreateLog("EMPLOYEE_CREATED", "Employee"));
        await db.SaveChangesAsync();

        var service = new AuditQueryService(db);
        var results = await service.SearchAsync(null, "Employee", null, null, 100, CancellationToken.None);

        var only = Assert.Single(results);
        Assert.Equal("EMPLOYEE_CREATED", only.Action);
    }

    /// <summary>The `take` parameter must never allow an unbounded query.</summary>
    [Fact]
    public async Task SearchAsync_TakeIsClampedToMaximum()
    {
        using var db = TestDbContext.CreateNew();
        for (var i = 0; i < 10; i++)
        {
            db.AuditLogs.Add(CreateLog($"ACTION_{i}", "Test", occurredAt: DateTimeOffset.UtcNow.AddMinutes(-i)));
        }
        await db.SaveChangesAsync();

        var service = new AuditQueryService(db);
        var results = await service.SearchAsync(null, null, null, null, take: 3, CancellationToken.None);

        Assert.Equal(3, results.Count);
    }
}
