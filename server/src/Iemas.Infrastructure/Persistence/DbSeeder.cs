using Iemas.Application.Common.Interfaces;
using Iemas.Domain.Ai;
using Iemas.Domain.Escalations;
using Iemas.Domain.Reminders;
using Iemas.Application.Operations;
using Iemas.Application.Notifications;
using Iemas.Domain.Identity;
using Iemas.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Iemas.Infrastructure.Persistence;

/// <summary>
/// Seeds the fixed RBAC role set (§85), a bootstrap Super Administrator account, and Phase 4
/// AI defaults so the CMS has an initial login and classification works without requiring manual
/// setup first. Idempotent — safe to run on every startup.
/// </summary>
public static class DbSeeder
{
    public static async Task SeedAsync(AppDbContext db, IPasswordHasher passwordHasher, IConfiguration configuration)
    {
        await db.Database.MigrateAsync();

        foreach (var roleName in SystemRole.All)
        {
            if (!await db.Roles.AnyAsync(r => r.Name == roleName))
            {
                db.Roles.Add(new Role { Name = roleName });
            }
        }
        await db.SaveChangesAsync();

        var adminEmail = configuration["Bootstrap:AdminEmail"];
        var adminPassword = configuration["Bootstrap:AdminPassword"];

        if (!string.IsNullOrWhiteSpace(adminEmail) && !string.IsNullOrWhiteSpace(adminPassword)
            && !await db.Users.AnyAsync(u => u.Email == adminEmail.ToLowerInvariant()))
        {
            var superAdminRole = await db.Roles.FirstAsync(r => r.Name == SystemRole.SuperAdministrator);

            var user = new User
            {
                Email = adminEmail.ToLowerInvariant(),
                DisplayName = "System Administrator",
                PasswordHash = passwordHasher.Hash(adminPassword),
                IsActive = true
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();

            db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = superAdminRole.Id });
            await db.SaveChangesAsync();
        }

        await SeedAiDefaultsAsync(db, configuration);
        await SeedNotificationTemplatesAsync(db);
        await SeedDefaultPoliciesAsync(db);
    }

    /// <summary>§51/§52 — the six predefined notification messages, editable afterwards in the CMS. Existing rows are never overwritten.</summary>
    private static async Task SeedNotificationTemplatesAsync(AppDbContext db)
    {
        var existing = await db.NotificationTemplates.Select(t => t.Type).ToListAsync();
        foreach (var type in Enum.GetValues<NotificationType>().Where(t => !existing.Contains(t)))
        {
            db.NotificationTemplates.Add(NotificationTemplateDefaults.For(type));
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Requirements §28 — the "Sales" profile example given verbatim in the requirements doc,
    /// seeded so classification is usable immediately. §82 — a single default OpenRouter model
    /// entry using a config-overridable identifier rather than a hardcoded model, per "Do not
    /// hardcode the current model catalog." The model row is seeded even without an API key
    /// configured; classification will cleanly fail to REVIEW_REQUIRED until a key is set (§83),
    /// rather than the system having no model to try at all.
    /// </summary>
    /// <summary>
    /// A new installation needs a default reminder and escalation policy, or no follow-up is ever sent for a Case
    /// nobody answers. Created only when no policy of that kind exists at all; administrators change them on the
    /// Reminder Policies / Escalation Policies pages.
    /// </summary>
    public static async Task SeedDefaultPoliciesAsync(AppDbContext db)
    {
        if (!await db.ReminderPolicies.AnyAsync())
        {
            // Business hours only make sense in the organisation's time zone; while it is still UTC (not set yet),
            // reminders are not limited to business hours rather than arriving at the wrong local time.
            var timeZone = await db.SystemSettings.Where(s => s.Key == SystemSettingKeys.DefaultTimeZone).Select(s => s.Value).FirstOrDefaultAsync();
            var hasTimeZone = !string.IsNullOrWhiteSpace(timeZone) && timeZone != "UTC" && TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _);
            db.ReminderPolicies.Add(new ReminderPolicy
            {
                Name = "Standard follow-up",
                Description = "Created automatically. Reminds the Case owner 1 hour after a Case is created and then every 2 hours, up to 3 times, until a reply is found in the Sent folder.",
                Enabled = true,
                IsDefault = true,
                InitialDelay = TimeSpan.FromHours(1),
                ReminderInterval = TimeSpan.FromHours(2),
                MaxReminders = 3,
                MinimumInterval = TimeSpan.FromMinutes(30),
                RestrictToBusinessHours = hasTimeZone,
                BusinessHoursStart = TimeSpan.FromHours(8),
                BusinessHoursEnd = TimeSpan.FromHours(17),
                ExcludeWeekends = hasTimeZone,
                TimeZoneId = hasTimeZone ? timeZone! : "UTC",
                ExpirationWindow = TimeSpan.FromDays(3),
            });
        }

        if (!await db.EscalationPolicies.AnyAsync())
        {
            var policy = new EscalationPolicy
            {
                Name = "Standard escalation",
                Description = "Created automatically. After 3 reminders without a reply and a 1-hour grace period the Case is escalated to the owner's supervisor, then 4 hours later to the department manager.",
                Enabled = true,
                IsDefault = true,
                TriggerReminderCount = 3,
                GracePeriod = TimeSpan.FromHours(1),
                Cooldown = TimeSpan.FromDays(1),
                MaximumLevel = 2,
                Channel = "Pop-up",
            };
            policy.Levels.Add(new EscalationLevel { Level = 1, DelayAfterPreviousLevel = TimeSpan.Zero, RecipientType = EscalationRecipientType.EmployeeSupervisor });
            policy.Levels.Add(new EscalationLevel { Level = 2, DelayAfterPreviousLevel = TimeSpan.FromHours(4), RecipientType = EscalationRecipientType.DepartmentManager });
            db.EscalationPolicies.Add(policy);
        }

        await db.SaveChangesAsync();
    }

    private static async Task SeedAiDefaultsAsync(AppDbContext db, IConfiguration configuration)
    {
        if (!await db.ClassificationProfiles.AnyAsync(p => p.Name == "Sales"))
        {
            db.ClassificationProfiles.Add(new ClassificationProfile
            {
                Name = "Sales",
                Description = "Default sales profile.",
                Enabled = true,
                Categories = string.Join('\n', new[]
                {
                    "PRODUCT_INQUIRY", "PRICE_REQUEST", "PRICE_LIST_REQUEST", "QUOTATION",
                    "PRODUCT_AVAILABILITY", "PURCHASE_DISCUSSION", "PURCHASE_VERIFICATION",
                    "CUSTOMER_REQUIREMENT", "CUSTOMER_SUPPORT", "CUSTOMER_COMPLAINT", "FOLLOW_UP",
                }),
                IncludeDefinitions = string.Join('\n', new[]
                {
                    "Product Inquiry", "Price Request", "Quotation", "Product Availability",
                    "Purchase Discussion", "Customer Requirement", "Follow-up",
                }),
                ExcludeDefinitions = string.Join('\n', new[]
                {
                    "Advertisement", "Newsletter", "E-commerce Notification", "Spam", "Software Advertisement",
                }),
            });
        }

        if (!await db.AiModelConfigs.AnyAsync())
        {
            var defaultModel = configuration["AiClassification:DefaultModelIdentifier"];
            db.AiModelConfigs.Add(new AiModelConfig
            {
                Provider = "OpenRouter",
                ModelIdentifier = string.IsNullOrWhiteSpace(defaultModel) ? "openai/gpt-4o-mini" : defaultModel,
                DisplayName = "Default Classification Model",
                Enabled = true,
                IsDefault = true,
                TaskCapability = "EmailClassification",
                TimeoutSeconds = 30,
                MaxRetries = 2,
                FallbackOrder = 0,
            });
        }

        await db.SaveChangesAsync();
    }
}
