using Iemas.Application.Reminders;
using Iemas.Application.Reminders.Dtos;
using Iemas.Domain.Reminders;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iemas.Tests.Reminders;

public class ReminderPolicyServiceTests
{
    private static SaveReminderPolicyRequest CreateRequest(
        string name = "Default", bool isDefault = true, int maxReminders = 3,
        IReadOnlyList<ReminderPolicyHolidayDto>? holidays = null)
    {
        return new SaveReminderPolicyRequest(
            name, "Description", true, isDefault, null,
            TimeSpan.FromHours(4), TimeSpan.FromHours(24), maxReminders, TimeSpan.FromHours(1),
            true, TimeSpan.FromHours(8), TimeSpan.FromHours(17), true, "UTC", TimeSpan.FromDays(3), null,
            holidays ?? Array.Empty<ReminderPolicyHolidayDto>());
    }

    private static ReminderPolicyService CreateService(TestDbContext db) => new(db, new NoOpAuditService());

    [Fact]
    public async Task CreateAsync_ValidRequest_PersistsPolicy()
    {
        using var db = TestDbContext.CreateNew();
        var service = CreateService(db);

        var result = await service.CreateAsync(CreateRequest(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, await db.ReminderPolicies.CountAsync());
    }

    /// <summary>§54 "No infinite reminder loops" enforced at validation too — MaxReminders must be at least 1.</summary>
    [Fact]
    public async Task CreateAsync_MaxRemindersZero_Fails()
    {
        using var db = TestDbContext.CreateNew();
        var service = CreateService(db);

        var result = await service.CreateAsync(CreateRequest(maxReminders: 0), CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task CreateAsync_DuplicateName_Fails()
    {
        using var db = TestDbContext.CreateNew();
        var service = CreateService(db);
        await service.CreateAsync(CreateRequest(name: "Default"), CancellationToken.None);

        var result = await service.CreateAsync(CreateRequest(name: "Default", isDefault: false), CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    /// <summary>Only one policy may be IsDefault at a time — setting a new default clears the old one.</summary>
    [Fact]
    public async Task CreateAsync_NewDefault_ClearsPreviousDefault()
    {
        using var db = TestDbContext.CreateNew();
        var service = CreateService(db);
        var first = await service.CreateAsync(CreateRequest(name: "First", isDefault: true), CancellationToken.None);

        var second = await service.CreateAsync(CreateRequest(name: "Second", isDefault: true), CancellationToken.None);

        Assert.True(second.Succeeded);
        var reloadedFirst = await db.ReminderPolicies.AsNoTracking().FirstAsync(p => p.Id == first.Value!.Id);
        Assert.False(reloadedFirst.IsDefault);
    }

    [Fact]
    public async Task CreateAsync_WithHolidays_PersistsHolidayRows()
    {
        using var db = TestDbContext.CreateNew();
        var service = CreateService(db);
        var holidays = new List<ReminderPolicyHolidayDto> { new(Guid.Empty, new DateOnly(2026, 12, 25), "Christmas") };

        var result = await service.CreateAsync(CreateRequest(holidays: holidays), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Single(result.Value!.Holidays);
        Assert.Equal("Christmas", result.Value.Holidays[0].Label);
    }

    [Fact]
    public async Task UpdateAsync_ExistingPolicy_UpdatesFields()
    {
        using var db = TestDbContext.CreateNew();
        var service = CreateService(db);
        var created = await service.CreateAsync(CreateRequest(), CancellationToken.None);

        var updated = await service.UpdateAsync(created.Value!.Id, CreateRequest(name: "Renamed"), CancellationToken.None);

        Assert.True(updated.Succeeded);
        Assert.Equal("Renamed", updated.Value!.Name);
    }

    [Fact]
    public async Task UpdateAsync_UnknownId_Fails()
    {
        using var db = TestDbContext.CreateNew();
        var service = CreateService(db);

        var result = await service.UpdateAsync(Guid.NewGuid(), CreateRequest(), CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    /// <summary>A policy with Scheduled reminders pending must not be deletable out from under them.</summary>
    [Fact]
    public async Task DeleteAsync_PolicyWithScheduledReminders_Fails()
    {
        using var db = TestDbContext.CreateNew();
        var service = CreateService(db);
        var created = await service.CreateAsync(CreateRequest(), CancellationToken.None);

        db.Reminders.Add(new Reminder
        {
            CaseId = Guid.NewGuid(),
            ReminderPolicyId = created.Value!.Id,
            Trigger = ReminderTrigger.InitialActionRequired,
            Status = ReminderStatus.Scheduled,
            SequenceNumber = 1,
            ScheduledForUtc = DateTimeOffset.UtcNow.AddHours(1),
        });
        await db.SaveChangesAsync();

        var result = await service.DeleteAsync(created.Value.Id, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(1, await db.ReminderPolicies.CountAsync());
    }

    [Fact]
    public async Task DeleteAsync_PolicyWithNoScheduledReminders_Succeeds()
    {
        using var db = TestDbContext.CreateNew();
        var service = CreateService(db);
        var created = await service.CreateAsync(CreateRequest(), CancellationToken.None);

        var result = await service.DeleteAsync(created.Value!.Id, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0, await db.ReminderPolicies.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_BusinessHoursStartAfterEnd_Fails()
    {
        using var db = TestDbContext.CreateNew();
        var service = CreateService(db);
        var request = new SaveReminderPolicyRequest(
            "Bad Hours", null, true, true, null,
            TimeSpan.FromHours(1), TimeSpan.FromHours(1), 3, TimeSpan.FromMinutes(30),
            true, TimeSpan.FromHours(18), TimeSpan.FromHours(9), true, "UTC", null, null,
            Array.Empty<ReminderPolicyHolidayDto>());

        var result = await service.CreateAsync(request, CancellationToken.None);

        Assert.False(result.Succeeded);
    }
}
