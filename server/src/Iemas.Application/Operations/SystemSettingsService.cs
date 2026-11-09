using System.Globalization;
using Iemas.Application.Common;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Common.Security;
using Iemas.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Operations;

public enum SettingValueType
{
    Text,
    Integer,
    TimeZone,
    SenderList,
    LongText,
    Boolean,
    RuleList,
}

public record SettingDefinition(
    string Key,
    string Group,
    string Label,
    string Description,
    SettingValueType Type,
    string DefaultValue,
    int? Min = null,
    int? Max = null);

public record SystemSettingDto(
    string Key,
    string Group,
    string Label,
    string Description,
    string Type,
    string Value,
    string DefaultValue,
    bool IsOverridden,
    int? Min,
    int? Max,
    string? UpdatedByEmail,
    DateTimeOffset? UpdatedAt);

public record UpdateSystemSettingsRequest(Dictionary<string, string> Values);

public record PublicSystemSettingsDto(string OrganizationName);

public static class SystemSettingKeys
{
    public const string OrganizationName = "general.organization_name";
    public const string DefaultTimeZone = "general.default_time_zone";
    public const string PasswordMinLength = "security.password_min_length";
    public const string IgnoredSenders = "email.ignored_senders";
    public const string LegitimacyRules = "ai.legitimacy_rules";
    public const string NotLegitimateRules = "ai.not_legitimate_rules";
    public const string ResponseRules = "ai.response_rules";
    public const string NoResponseRules = "ai.no_response_rules";
    public const string RequireResponseForCase = "ai.require_response_for_case";

    // Inquiry detection (Zettelkasten filter) - see inquiry-supervision/PROGRESS.md Phase 1.
    public const string InternalDomains = "inquiry.internal_domains";
    public const string InquiryPhrases = "inquiry.inquiry_phrases";
    public const string NotInquiryPhrases = "inquiry.not_inquiry_phrases";
    public const string AutomatedSenderPatterns = "inquiry.automated_sender_patterns";
    public const string InquiryScoreThreshold = "inquiry.inquiry_score_threshold";
    public const string NotInquiryScoreThreshold = "inquiry.not_inquiry_score_threshold";

    // Inquiry supervision timers - Q1 decisions (inquiry-supervision/PROGRESS.md section 5).
    public const string SupervisionEnabled = "inquiry.supervision_enabled";
    public const string FirstResponseBusinessHours = "inquiry.first_response_business_hours";
    public const string ReminderIntervalBusinessHours = "inquiry.reminder_interval_business_hours";
    public const string RemindersBeforeEscalation = "inquiry.reminders_before_escalation";
    public const string IdleBusinessDays = "inquiry.idle_business_days";
    public const string CustomerWaitCalendarDays = "inquiry.customer_wait_calendar_days";
    public const string InternalWaitBusinessDays = "inquiry.internal_wait_business_days";
    public const string ResolutionTargetBusinessDays = "inquiry.resolution_target_business_days";
    public const string EscalationRepeatBusinessDays = "inquiry.escalation_repeat_business_days";

    // AI resolution assessment - Q2 decisions.
    public const string AiResolutionEnabled = "inquiry.ai_resolution_enabled";
    public const string TestClockMinutes = "inquiry.test_clock_minutes";
    public const string AiResolutionMinConfidence = "inquiry.ai_resolution_min_confidence_percent";

    // Stored agent replies - Q4 decisions.
    public const string AgentReplyRetentionDays = "inquiry.agent_reply_retention_days";
}

public static class SystemSettingDefaults
{
    public static readonly string InquiryPhrases = string.Join('\n',
        "request a quotation", "request for quotation", "request a quote", "quotation for", "quote for",
        "price list", "pricing", "how much", "what is the price", "interested in your", "interested in buying",
        "would like to inquire", "would like to enquire", "inquiry about", "enquiry about", "would like to order",
        "place an order", "availability of", "do you have", "can you provide", "could you send",
        "more information about", "product catalog", "brochure", "become a distributor", "become a dealer",
        "discuss a possible project", "help us with", "can you help");

    public static readonly string NotInquiryPhrases = string.Join('\n',
        "unsubscribe", "view this email in your browser", "this is an automated message", "do not reply to this email",
        "this email was sent automatically", "payment received", "payment confirmation", "your order has shipped",
        "delivery status notification", "out of office", "automatic reply", "auto-reply", "password reset",
        "verify your email", "webinar", "newsletter", "you are receiving this email because");

    public static readonly string AutomatedSenderPatterns = string.Join('\n',
        "noreply", "no-reply", "donotreply", "do-not-reply", "mailer-daemon", "postmaster", "bounce",
        "notifications@", "notification@", "newsletter", "alerts@", "marketing@");

    public static readonly string LegitimacyRules = string.Join('\n',
        "Written by a real person or company that does business with us (customer, supplier, partner or colleague)",
        "About a real business matter: orders, quotes, products, deliveries, payments, support or complaints");

    public static readonly string NotLegitimateRules = string.Join('\n',
        "Spam, phishing or scam attempts",
        "Mass marketing, promotions and newsletters",
        "Automated notifications: delivery updates, system alerts, receipts, calendar invites",
        "Out-of-office and other auto-replies",
        "Test messages");

    public static readonly string ResponseRules = string.Join('\n',
        "The sender asks a question",
        "The sender requests a quote, price, information, documents or an action",
        "The sender reports a problem or complaint",
        "The sender is waiting for a decision or approval from us");

    public static readonly string NoResponseRules = string.Join('\n',
        "FYI or CC-only updates",
        "Thank-you or acknowledgement messages",
        "Reports or documents sent for information only",
        "Conversations that are already concluded");
}

/// <summary>
/// A fixed catalog of system settings (no free-form keys), each with a validated type and a
/// default. Only overrides are stored. Retention values (§90) are recorded policy; no automatic
/// purge job acts on them yet.
/// </summary>
public class SystemSettingsService
{
    private const string RetentionDescription =
        "Days to keep; 0 keeps indefinitely. Recorded retention policy only; no automatic clean-up acts on it yet.";

    public static readonly IReadOnlyList<SettingDefinition> Catalog = new List<SettingDefinition>
    {
        new(SystemSettingKeys.OrganizationName, "General", "Organization name",
            "Shown in the CMS header.", SettingValueType.Text, "IEMAS", 1, 100),
        new(SystemSettingKeys.DefaultTimeZone, "General", "Default time zone",
            "IANA time zone (e.g. Asia/Manila) used to display dates in notification messages.", SettingValueType.TimeZone, "UTC"),
        new(SystemSettingKeys.LegitimacyRules, "Email rules", "Counts as legitimate",
            "Signs of a genuine business email.", SettingValueType.RuleList, SystemSettingDefaults.LegitimacyRules, 0, 4000),
        new(SystemSettingKeys.NotLegitimateRules, "Email rules", "Not legitimate",
            "Email like this never becomes a Case.", SettingValueType.RuleList, SystemSettingDefaults.NotLegitimateRules, 0, 4000),
        new(SystemSettingKeys.ResponseRules, "Email rules", "Needs a response",
            "The sender expects a reply from us when…", SettingValueType.RuleList, SystemSettingDefaults.ResponseRules, 0, 4000),
        new(SystemSettingKeys.NoResponseRules, "Email rules", "No response needed",
            "No reply is expected when…", SettingValueType.RuleList, SystemSettingDefaults.NoResponseRules, 0, 4000),
        new(SystemSettingKeys.RequireResponseForCase, "Email rules", "Create a Case only when a response is needed",
            "On: relevant, legitimate email that needs no reply (FYI, thank-you, reports) is stored but does not become a Case. Off: every relevant, legitimate email becomes a Case.",
            SettingValueType.Boolean, "true"),
        new(SystemSettingKeys.IgnoredSenders, "Email filtering", "Ignored senders",
            "One per line: a domain (sawo.com — also covers its subdomains) or a full address (noreply@shop.com). "
            + "Their email is stored but never classified, never becomes a Case and never notifies anyone; "
            + "reminders and escalations stop for their existing Cases.",
            SettingValueType.SenderList, "", 0, 4000),
        new(SystemSettingKeys.InternalDomains, "Inquiry detection", "Internal company domains",
            "One per line: a domain (sawo.com) or a full address. Email from these senders never starts a new customer inquiry, "
            + "but stays linked to an existing inquiry when it belongs to that conversation (for example an internal forward).",
            SettingValueType.SenderList, "sawo.com", 0, 4000),
        new(SystemSettingKeys.InquiryPhrases, "Inquiry detection", "Inquiry phrases",
            "Phrases that suggest a customer inquiry. Each distinct match adds 2 points.", SettingValueType.RuleList, SystemSettingDefaults.InquiryPhrases, 0, 9000),
        new(SystemSettingKeys.NotInquiryPhrases, "Inquiry detection", "Not-an-inquiry phrases",
            "Phrases typical of newsletters, notices and auto-replies. Each distinct match removes 3 points.", SettingValueType.RuleList, SystemSettingDefaults.NotInquiryPhrases, 0, 9000),
        new(SystemSettingKeys.AutomatedSenderPatterns, "Inquiry detection", "Automated sender patterns",
            "Text found in automated sender addresses (noreply, notifications@ ...). A match removes 4 points.", SettingValueType.RuleList, SystemSettingDefaults.AutomatedSenderPatterns, 0, 9000),
        new(SystemSettingKeys.InquiryScoreThreshold, "Inquiry detection", "Score to accept as an inquiry without AI",
            "At or above this score, with no negative signal, the email is treated as an inquiry without calling the AI.", SettingValueType.Integer, "5", 1, 50),
        new(SystemSettingKeys.NotInquiryScoreThreshold, "Inquiry detection", "Score to reject without AI",
            "At or below minus this score the email is treated as not an inquiry without calling the AI. Anything in between goes to the AI.", SettingValueType.Integer, "3", 1, 50),
        new(SystemSettingKeys.SupervisionEnabled, "Inquiry supervision", "Supervise inquiries until resolved",
            "On: inquiries are followed through every stage until the agent confirms they are resolved, with reminders and management escalation. "
            + "Off: the older reminder and escalation engines are used, which stop at the first reply.", SettingValueType.Boolean, "true"),
        new(SystemSettingKeys.FirstResponseBusinessHours, "Inquiry supervision", "Initial response time (business hours)",
            "How long the agent has to send a first reply to a new inquiry.", SettingValueType.Integer, "4", 1, 240),
        new(SystemSettingKeys.ReminderIntervalBusinessHours, "Inquiry supervision", "Reminder interval (business hours)",
            "Time from a missed deadline to the first reminder, between reminders, and from the last reminder to escalation.", SettingValueType.Integer, "2", 1, 240),
        new(SystemSettingKeys.RemindersBeforeEscalation, "Inquiry supervision", "Reminders before management escalation",
            "How many reminders the agent gets before management is told.", SettingValueType.Integer, "2", 0, 10),
        new(SystemSettingKeys.IdleBusinessDays, "Inquiry supervision", "Conversation idle after (business days)",
            "When the agent owes the next step (for example after a customer follow-up) and nothing happens for this long, the inquiry is idle.", SettingValueType.Integer, "1", 1, 30),
        new(SystemSettingKeys.CustomerWaitCalendarDays, "Inquiry supervision", "Waiting for customer (calendar days)",
            "After this long without a customer reply the agent is told a follow-up may be needed. Never closes the inquiry.", SettingValueType.Integer, "3", 1, 60),
        new(SystemSettingKeys.InternalWaitBusinessDays, "Inquiry supervision", "Waiting for internal action (business days)",
            "After this long waiting on another department with no activity, the agent is asked for an update.", SettingValueType.Integer, "1", 1, 60),
        new(SystemSettingKeys.ResolutionTargetBusinessDays, "Inquiry supervision", "Resolution target (business days)",
            "An inquiry still unresolved after this long is marked overdue. A target only: it never closes the inquiry.", SettingValueType.Integer, "5", 1, 120),
        new(SystemSettingKeys.EscalationRepeatBusinessDays, "Inquiry supervision", "Escalate to the next level after (business days)",
            "If an escalated inquiry is still neglected after this long, the next escalation level is notified.", SettingValueType.Integer, "1", 1, 60),
        new(SystemSettingKeys.TestClockMinutes, "Inquiry supervision", "TEST MODE: count hours as minutes",
            "For trying the system out only. On: every supervision time above runs 60 times faster (1 hour = 1 minute, 1 day = 24 minutes) "
            + "and business hours, weekends and holidays are ignored. Turn it OFF before real use.", SettingValueType.Boolean, "false"),
        new(SystemSettingKeys.AiResolutionEnabled, "Inquiry supervision", "AI checks whether inquiries look resolved",
            "The AI reads the conversation and may mark an inquiry Potentially resolved. Only the agent can confirm it resolved.", SettingValueType.Boolean, "true"),
        new(SystemSettingKeys.AiResolutionMinConfidence, "Inquiry supervision", "AI confidence needed for Potentially resolved (%)",
            "The AI must be at least this confident before an inquiry is marked Potentially resolved.", SettingValueType.Integer, "75", 50, 100),
        new(SystemSettingKeys.AgentReplyRetentionDays, "Inquiry supervision", "Keep stored agent replies for (days)",
            "The encrypted text of agent replies is erased this many days after the inquiry is closed. 0 keeps it.", SettingValueType.Integer, "365", 0, 36500),
        new(SystemSettingKeys.PasswordMinLength, "Security", "Minimum password length",
            "Enforced when creating users and changing or resetting passwords.", SettingValueType.Integer, "10", 8, 128),
        new("retention.raw_email_content", "Retention", "Raw email content", RetentionDescription, SettingValueType.Integer, "0", 0, 36500),
        new("retention.email_metadata", "Retention", "Email metadata", RetentionDescription, SettingValueType.Integer, "0", 0, 36500),
        new("retention.cases", "Retention", "Cases", RetentionDescription, SettingValueType.Integer, "0", 0, 36500),
        new("retention.case_history", "Retention", "Case History", RetentionDescription, SettingValueType.Integer, "0", 0, 36500),
        new("retention.ai_results", "Retention", "AI results", RetentionDescription, SettingValueType.Integer, "0", 0, 36500),
        new("retention.employee_activity", "Retention", "Employee Activity", RetentionDescription, SettingValueType.Integer, "0", 0, 36500),
        new("retention.audit_logs", "Retention", "Audit Logs", RetentionDescription, SettingValueType.Integer, "0", 0, 36500),
        new("retention.agent_logs", "Retention", "Agent technical logs", RetentionDescription, SettingValueType.Integer, "0", 0, 36500),
        new("retention.outbound_email_logs", "Retention", "Outbound email logs", RetentionDescription, SettingValueType.Integer, "0", 0, 36500),
    };

    private readonly IAppDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditService _auditService;

    public SystemSettingsService(IAppDbContext db, ICurrentUserService currentUser, IAuditService auditService)
    {
        _db = db;
        _currentUser = currentUser;
        _auditService = auditService;
    }

    public static async Task<string> GetValueAsync(IAppDbContext db, string key, CancellationToken cancellationToken)
    {
        var definition = Catalog.First(d => d.Key == key);
        var stored = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == key)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);
        return stored ?? definition.DefaultValue;
    }

    public static async Task<bool> GetBoolAsync(IAppDbContext db, string key, CancellationToken cancellationToken) =>
        await GetValueAsync(db, key, cancellationToken) == "true";

    public static async Task<int> GetIntAsync(IAppDbContext db, string key, CancellationToken cancellationToken) =>
        int.Parse(await GetValueAsync(db, key, cancellationToken), CultureInfo.InvariantCulture);

    public static async Task<TimeZoneInfo> GetDefaultTimeZoneAsync(IAppDbContext db, CancellationToken cancellationToken)
    {
        var id = await GetValueAsync(db, SystemSettingKeys.DefaultTimeZone, cancellationToken);
        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Utc;
    }

    public async Task<List<SystemSettingDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        var stored = await _db.SystemSettings.AsNoTracking().ToListAsync(cancellationToken);
        return Catalog.Select(d =>
        {
            var row = stored.FirstOrDefault(s => s.Key == d.Key);
            return new SystemSettingDto(
                d.Key, d.Group, d.Label, d.Description, d.Type.ToString(),
                row?.Value ?? d.DefaultValue, d.DefaultValue, row is not null,
                d.Min, d.Max, row?.UpdatedByEmail, row?.UpdatedAt ?? row?.CreatedAt);
        }).ToList();
    }

    public async Task<PublicSystemSettingsDto> GetPublicAsync(CancellationToken cancellationToken) =>
        new(await GetValueAsync(_db, SystemSettingKeys.OrganizationName, cancellationToken));

    /// <summary>All-or-nothing: every submitted value is validated before any is written.</summary>
    public async Task<Result<List<SystemSettingDto>>> UpdateAsync(UpdateSystemSettingsRequest request, CancellationToken cancellationToken)
    {
        if (request.Values is null || request.Values.Count == 0)
        {
            return Result<List<SystemSettingDto>>.Failure("No settings were submitted.");
        }

        var normalized = new Dictionary<string, string>();
        foreach (var (key, rawValue) in request.Values)
        {
            var definition = Catalog.FirstOrDefault(d => d.Key == key);
            if (definition is null)
            {
                return Result<List<SystemSettingDto>>.Failure($"Unknown setting '{key}'.");
            }

            var (value, error) = Validate(definition, rawValue);
            if (error is not null)
            {
                return Result<List<SystemSettingDto>>.Failure(error);
            }
            normalized[key] = value!;
        }

        var keys = normalized.Keys.ToList();
        var existing = await _db.SystemSettings.Where(s => keys.Contains(s.Key)).ToListAsync(cancellationToken);
        var changes = new List<(string Key, string Detail)>();

        foreach (var (key, value) in normalized)
        {
            var row = existing.FirstOrDefault(s => s.Key == key);
            var oldValue = row?.Value ?? Catalog.First(d => d.Key == key).DefaultValue;
            if (row is null)
            {
                row = new SystemSetting { Key = key };
                _db.SystemSettings.Add(row);
            }
            else if (row.Value == value)
            {
                continue;
            }

            row.Value = value;
            row.UpdatedByUserId = _currentUser.UserId;
            row.UpdatedByEmail = _currentUser.Email;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            if (oldValue != value) changes.Add((key, $"{key}: '{oldValue}' -> '{value}'"));
        }

        await _db.SaveChangesAsync(cancellationToken);

        foreach (var (key, detail) in changes)
        {
            await _auditService.LogAsync("SYSTEM_SETTING_UPDATED", "SystemSetting", key, detail, cancellationToken);
        }

        if (changes.Any(c => c.Key == SystemSettingKeys.IgnoredSenders))
        {
            await ReapplyIgnoredSendersAsync(cancellationToken);
        }

        return Result<List<SystemSettingDto>>.Success(await GetAllAsync(cancellationToken));
    }

    public async Task<Result<bool>> ResetAsync(string key, CancellationToken cancellationToken)
    {
        var definition = Catalog.FirstOrDefault(d => d.Key == key);
        if (definition is null)
        {
            return Result<bool>.Failure($"Unknown setting '{key}'.");
        }

        var row = await _db.SystemSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (row is null)
        {
            return Result<bool>.Success(true);
        }

        var oldValue = row.Value;
        _db.SystemSettings.Remove(row);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.LogAsync("SYSTEM_SETTING_RESET", "SystemSetting", key,
            $"{key}: '{oldValue}' -> default '{definition.DefaultValue}'", cancellationToken);
        if (key == SystemSettingKeys.IgnoredSenders)
        {
            await ReapplyIgnoredSendersAsync(cancellationToken);
        }
        return Result<bool>.Success(true);
    }

    /// <summary>
    /// Brings stored email in line with the current Ignored senders list: waiting email from a listed
    /// sender becomes Ignored, and Ignored email whose sender is no longer listed goes back into
    /// processing (or to Historical if it predates the mailbox cut-off).
    /// </summary>
    private async Task ReapplyIgnoredSendersAsync(CancellationToken cancellationToken)
    {
        var list = await IgnoredSenderList.LoadAsync(_db, cancellationToken);
        var candidates = await _db.EmailMessages
            .Where(m => m.ProcessingStatus == Domain.Email.EmailProcessingStatus.PendingClassification
                        || m.ProcessingStatus == Domain.Email.EmailProcessingStatus.Ignored)
            .Select(m => new { Message = m, Cutoff = m.EmailAccount.ProcessEmailsReceivedAfter })
            .ToListAsync(cancellationToken);

        int ignored = 0, restored = 0;
        foreach (var c in candidates)
        {
            var matches = list.Matches(c.Message.FromAddress);
            if (matches && c.Message.ProcessingStatus == Domain.Email.EmailProcessingStatus.PendingClassification)
            {
                c.Message.ProcessingStatus = Domain.Email.EmailProcessingStatus.Ignored;
                ignored++;
            }
            else if (!matches && c.Message.ProcessingStatus == Domain.Email.EmailProcessingStatus.Ignored)
            {
                c.Message.ProcessingStatus = c.Cutoff is DateTimeOffset cutoff && c.Message.ReceivedAt < cutoff
                    ? Domain.Email.EmailProcessingStatus.Historical
                    : Domain.Email.EmailProcessingStatus.PendingClassification;
                restored++;
            }
        }
        await _db.SaveChangesAsync(cancellationToken);

        var openCaseCustomers = await _db.Cases
            .Where(c => c.WorkStatus != Domain.Cases.CaseWorkStatus.Completed && c.WorkStatus != Domain.Cases.CaseWorkStatus.Cancelled)
            .Select(c => c.CustomerEmailAddress)
            .ToListAsync(cancellationToken);
        var silencedCases = openCaseCustomers.Count(list.Matches);

        await _auditService.LogAsync("IGNORED_SENDERS_APPLIED", "SystemSetting", SystemSettingKeys.IgnoredSenders,
            $"{ignored} waiting email(s) set Ignored, {restored} Ignored email(s) returned to processing; "
            + $"{silencedCases} open Case(s) from listed senders get no further reminders, escalations or notifications",
            cancellationToken);
    }

    private static (string? Value, string? Error) Validate(SettingDefinition definition, string? rawValue)
    {
        var value = rawValue?.Trim() ?? string.Empty;
        switch (definition.Type)
        {
            case SettingValueType.Integer:
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                {
                    return (null, $"{definition.Label} must be a whole number.");
                }
                if (number < definition.Min || number > definition.Max)
                {
                    return (null, $"{definition.Label} must be between {definition.Min} and {definition.Max}.");
                }
                return (number.ToString(CultureInfo.InvariantCulture), null);

            case SettingValueType.SenderList:
            {
                var (normalized, listError) = IgnoredSenderList.Normalize(value);
                if (listError is not null) return (null, $"{definition.Label}: {listError}");
                if (normalized!.Length > (definition.Max ?? 4000)) return (null, $"{definition.Label} is too long.");
                return (normalized, null);
            }

            case SettingValueType.RuleList:
            {
                var rules = value.Replace("\r\n", "\n").Split('\n')
                    .Select(r => r.Trim().TrimStart('-', '*', '•').Trim())
                    .Where(r => r.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (rules.Count > 30) return (null, $"{definition.Label} can have at most 30 rules.");
                var tooLong = rules.FirstOrDefault(r => r.Length > 300);
                if (tooLong is not null) return (null, $"{definition.Label}: each rule can be at most 300 characters.");
                foreach (var rule in rules)
                {
                    if (InputSanitizer.ValidateFreeText(definition.Label, rule) is { } ruleError) return (null, ruleError);
                }
                return (string.Join('\n', rules), null);
            }

            case SettingValueType.Boolean:
                return value.ToLowerInvariant() switch
                {
                    "true" or "on" or "yes" or "1" => ("true", null),
                    "false" or "off" or "no" or "0" => ("false", null),
                    _ => (null, $"{definition.Label} must be true or false."),
                };

            case SettingValueType.LongText:
                if (value.Length > (definition.Max ?? 2000))
                {
                    return (null, $"{definition.Label} cannot exceed {definition.Max ?? 2000} characters.");
                }
                return InputSanitizer.ValidateFreeText(definition.Label, value) is { } longTextError ? (null, longTextError) : (value.Replace("\r\n", "\n"), null);

            case SettingValueType.TimeZone:
                if (!TimeZoneInfo.TryFindSystemTimeZoneById(value, out _))
                {
                    return (null, $"'{value}' is not a recognized time zone.");
                }
                return (value, null);

            default:
                if (value.Length < (definition.Min ?? 0) || value.Length > (definition.Max ?? 1000))
                {
                    return (null, $"{definition.Label} must be between {definition.Min ?? 0} and {definition.Max ?? 1000} characters.");
                }
                if (InputSanitizer.ValidateFreeText(definition.Label, value) is { } error)
                {
                    return (null, error);
                }
                return (value, null);
        }
    }
}
