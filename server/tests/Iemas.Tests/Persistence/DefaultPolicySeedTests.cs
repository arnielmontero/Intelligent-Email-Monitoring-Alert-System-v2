using Iemas.Domain.Escalations;
using Iemas.Domain.Reminders;
using Iemas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iemas.Tests.Persistence;

/// <summary>A new installation must follow up on unanswered Cases without anyone creating policies first.</summary>
public class DefaultPolicySeedTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    // The full SeedAsync also applies migrations, which needs PostgreSQL; the policy step is self-contained.
    private static Task SeedAsync(AppDbContext db) => DbSeeder.SeedDefaultPoliciesAsync(db);

    [Fact]
    public async Task FreshInstall_GetsDefaultReminderAndEscalationPolicies()
    {
        using var db = NewDb();

        await SeedAsync(db);

        var reminder = await db.ReminderPolicies.SingleAsync();
        Assert.True(reminder.IsDefault && reminder.Enabled);
        Assert.Equal(TimeSpan.FromHours(1), reminder.InitialDelay);
        Assert.False(reminder.RestrictToBusinessHours); // no time zone set yet, so no business-hours window in UTC

        var escalation = await db.EscalationPolicies.Include(p => p.Levels).SingleAsync();
        Assert.True(escalation.IsDefault && escalation.Enabled);
        Assert.Equal(EscalationRecipientType.EmployeeSupervisor, escalation.Levels.Single(l => l.Level == 1).RecipientType);
    }

    [Fact]
    public async Task ExistingPolicies_AreNeverReplacedOrDuplicated()
    {
        using var db = NewDb();
        db.ReminderPolicies.Add(new ReminderPolicy { Name = "Mine", IsDefault = true });
        db.EscalationPolicies.Add(new EscalationPolicy { Name = "Mine", IsDefault = true });
        await db.SaveChangesAsync();

        await SeedAsync(db);
        await SeedAsync(db);

        Assert.Equal("Mine", (await db.ReminderPolicies.SingleAsync()).Name);
        Assert.Equal("Mine", (await db.EscalationPolicies.SingleAsync()).Name);
    }
}
