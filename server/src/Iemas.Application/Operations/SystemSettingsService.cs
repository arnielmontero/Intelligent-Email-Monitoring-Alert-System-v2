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
    int? Max = null,
    /// <summary>What the number counts, shown beside the input: "business hours", "calendar days", "%", "points", ...</summary>
    string? Unit = null);

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
    DateTimeOffset? UpdatedAt,
    string? Unit = null);

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
    public const string ProductTerms = "inquiry.product_terms";
    public const string RequestCues = "inquiry.request_cues";
    public const string DealerPhrases = "inquiry.dealer_phrases";
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
        "more information about", "product catalog", "brochure",
        "discuss a possible project", "help us with", "can you help",
        // Learned from real SAWO web-form / info@ inquiries (inquiries_export.csv): buyers ask for prices and quotes,
        // where to buy, spare parts, manuals, shipping and dealer terms - often in a single short line.
        // "customize my sauna" is the subject of the website's sauna configurator form.
        "customize my sauna", "quote", "quotation", "price for", "price of", "the price", "best price", "prices", "price guide", "pricing options",
        "how much would", "how much is", "how much does", "how much will", "cost of", "costs", "the cost", "send me", "please send", "pls send",
        "please provide", "kindly quote", "kindly provide", "please advise", "kindly advise",
        "where can i buy", "where to buy", "where can i get", "where can i purchase", "from where to buy", "i would like to buy", "i would like to purchase",
        "i want to buy", "i want to purchase", "i'd like to purchase", "looking to purchase", "looking to buy", "interested in purchasing",
        "would like to purchase", "would like to get", "want to order", "can we order", "can i order", "how to order", "how can i buy", "how can i purchase",
        "do you ship", "ship to", "shipping to", "deliver to", "deliver in", "delivered to", "delivery time", "lead time", "catalogue", "catalog",
        "dealer", "distributor", "supplier in", "spare part", "replacement", "replace my", "need to replace", "user manual", "manual for",
        "instruction manual", "specification", "cad drawing", "is it possible", "compatible", "warranty", "repair", "not working", "doesn't work",
        "stopped working", "broken", "malfunction", "error e",
        // Common non-English enquiries (German, French, Italian, Spanish, Finnish, Swedish, Dutch, Tagalog, Thai, Japanese).
        "anfrage", "preis", "angebot", "wo kann ich", "preventivo", "prezzo", "devis", "prix", "combien", "tarjous", "hinta", "paljonko",
        "presupuesto", "cotización", "cotizacion", "precio", "repuesto", "prijs", "offerte", "pris på", "vad kostar", "magkano", "ขอราคา", "ราคา", "見積", "価格");

    /// <summary>
    /// Phrases a business uses when it wants to resell, distribute, wholesale, represent or partner with SAWO.
    /// A confident inquiry with any of these is labelled "Dealer / Reseller" instead of "Inquiry".
    /// Deliberately not "dealer" alone: end customers ask "is there a dealer near me?".
    /// </summary>
    public static readonly string DealerPhrases = string.Join('\n',
        "become a dealer", "become a distributor", "become a reseller", "become your", "dealer inquiry", "dealer enquiry", "dealer request",
        "dealer application", "dealer program", "dealer pricing", "dealer price", "dealer prices", "dealer partnership", "dealer account",
        "authorized dealer", "authorised dealer", "reseller", "resell", "wholesale", "distribution", "distributorship", "exclusive distributor",
        "distributor for", "distributor in our", "agent in", "partnership", "partner with", "collaboration", "cooperation", "co-operation",
        "oem", "private label", "our store", "our retail", "retail store", "online store", "our e-commerce", "e-commerce store",
        "ecommerce", "dropship", "drop ship", "carry your", "add your products", "stock your", "sales conditions", "terms of cooperation",
        "bulk order", "for our market", "in our country", "trade price", "trade account", "for the syrian market", "for the usa market",
        "distribución", "para nuestro país", "distribuzione", "rivenditore", "revendeur", "händler werden", "jälleenmyyjä");

    /// <summary>
    /// SAWO / sauna product vocabulary in the languages customers write in. Product context is what makes a
    /// one-line web-form message ("Need 6 kw sauna heater") a confident inquiry without AI.
    /// </summary>
    public static readonly string ProductTerms = string.Join('\n',
        "sauna", "heater", "stove", "steam", "generator", "infrared", "infared", "controller", "control unit", "control panel",
        "control box", "power controller", "heating element", "elements", "thermostat", "sensor", "sauna stones", "stones",
        "sauna door", "door handle", "hinge", "bucket", "pail", "ladle", "thermometer", "hygrometer", "bench", "aroma",
        "sauna room", "sauna kit", "sauna cabin", "steam room", "steam shower", "vaporizer", "timer", "wifi", "liner", "coals",
        "wooden door", "glass door", "sauna handle", "relay", "steam unit",
        "saunova", "innova", "sawotec", "nordex", "scandia", "scandifire", "krios", "aries", "cumulus", "cubos", "tower round",
        "tower corner", "savonia", "nimbus", "taurus", "dragonfire", "mini x", "mini combi", "sawo mini", "super savonia", "phoenix", "rock",
        "kiuas", "kiukaa", "vastus", "vastukset", "ohjauspaneeli", "den ofen", "der ofen", "einen ofen", "ofenhändler", "saunaofen", "heizstab", "steuerteil", "steuergerät", "dampfgenerator",
        "poêle", "poele", "pierres", "vapeur", "stufa", "generatore di vapore", "calefactor", "estufa", "generador", "vapor", "bastu", "aggregaat",
        "サウナ", "ストーン", "ซาวน่า", "ไอน้ำ", "ห้องอบ");

    /// <summary>Weak intent cues: alone they never decide, together with product context they do.</summary>
    public static readonly string RequestCues = string.Join('\n',
        "i need", "we need", "need a", "need to", "i want", "we want", "looking for", "looking to", "interested", "would like",
        "i'd like", "we'd like", "i like to", "require", "requirement", "inquire", "enquire", "inquiry", "enquiry", "inquiring", "please", "pls",
        "kindly", "advise", "let me know", "wondering", "planning", "asap", "urgent", "need", "recommend", "available", "availability",
        "installation", "install", "contact me", "call me", "still waiting", "waiting for", "no reply", "can i get", "can we get",
        "when will", "how often", "how long", "how to", "suitable", "do you sell", "do you make", "do you offer", "do you do", "is there",
        "which", "want to know", "would like to know", "wish to know", "can't find", "cannot find", "any details",
        "estamos interesados", "interesados", "medidas", "miten", "kannattaa", "utelisin", "tiedättekö", "ถาม",
        "ich möchte", "ich suche", "bitte", "gibt es", "können sie", "kann ich", "erbitte", "je voudrais", "je cherche", "pouvez-vous",
        "quiero", "necesito", "necesitamos", "estoy buscando", "me gustaría", "podría", "vorrei", "cerco", "haluaisin", "tarvitsen",
        "tarvitaan", "kaivattaisiin", "onko", "voiko", "jag vill", "ik wil", "pwede", "รบกวน", "ขอ", "したい", "ください");

    public static readonly string NotInquiryPhrases = string.Join('\n',
        "unsubscribe", "view this email in your browser", "this is an automated message", "do not reply to this email",
        "this email was sent automatically", "payment received", "payment confirmation", "your order has shipped",
        "delivery status notification", "out of office", "automatic reply", "auto-reply", "password reset",
        "verify your email", "webinar", "newsletter", "you are receiving this email because",
        // Promotional / renewal marketing (e.g. a vendor's "Renew before ... current pricing" mail).
        "manage your subscription", "email preferences", "update your preferences", "renew before", "renew now",
        "limited time", "special offer", "exclusive offer", "save up to", "% off", "black friday", "cyber monday",
        "view in browser", "view it in your browser", "your license expires", "your subscription expires",
        // Cold vendor outreach seen in the inbox: trade-show attendee/contact lists, exhibitor mailings, SEO/Wikipedia
        // services and "we introduce our company" pitches. These sell TO SAWO; they are not customer inquiries.
        "attendee list", "attendees list", "attendee email", "attendee data", "email list", "mailing list", "contact database",
        "industry database", "verified database", "targeted lists", "business contact information", "send counts and pricing",
        "dear exhibitor", "greetings exhibitors", "discounted hotel", "hotel rooms", "is exhibiting at", "are exhibiting at",
        "trade show booth", "wikipedia page", "backlinks", "guest post", "search engine optimization",
        "exclusive supplier of", "formally introduce", "iso/ce/rohs", "leading exporter of", "abbestellen",
        "one of the leading", "leading manufacturer", "leading supplier", "professional manufacturer", "introduce our company",
        "introduce our products", "would like to introduce", "allow me to introduce", "our product range includes", "our core customers",
        "competitive price", "factory direct", "free sample", "reach out to see if", "would you be interested in", "are you planning any",
        "your sourcing department", "your purchasing department", "based specialist in", "surface treatment", "lead generation",
        "marketing campaigns", "prospecting", "is conducting a profiling", "designated establishments",
        // Generic service offers (agencies, outsourcing, consultants pitching their services to us).
        "our services", "i came across your website", "came across your company", "grow your business", "increase your sales",
        "more leads", "qualified leads", "appointment setting", "virtual assistant", "web design", "website redesign", "app development",
        "digital marketing", "social media marketing", "free consultation", "free audit", "15-minute call", "15 minute call",
        "hop on a call", "outsourcing", "we help companies", "we help businesses", "helping businesses", "rank higher",
        "first page of google", "seo services", "our agency", "white label", "case studies",
        // Consumer-style promotions (a business advertising its own services/packages to us).
        "dear valued customer", "valued customers", "pleased to introduce", "we are pleased to offer", "our services include",
        "our packages", "packages for individuals", "contact us today", "book now", "book your", "we look forward to serving you",
        "look forward to serving", "exclusive deal", "promo code",
        // Logistics / accounting notices about orders already placed.
        "tracking number:", "has been dispatched", "proof of delivery", "remittance advice", "statement of account");

    public static readonly string AutomatedSenderPatterns = string.Join('\n',
        "noreply", "no-reply", "donotreply", "do-not-reply", "mailer-daemon", "postmaster", "bounce",
        "notifications@", "notification@", "newsletter", "alerts@", "marketing@",
        "list@", "mailer@", "mailing@", "promo@", "promotions@", "offers@", "deals@", "news@", "updates@", "info-noreply");

    public static readonly string LegitimacyRules = string.Join('\n',
        "Written by a real person or company that does business with us (customer, supplier, partner or colleague)",
        "About a real business matter: orders, quotes, products, deliveries, payments, support or complaints");

    public static readonly string NotLegitimateRules = string.Join('\n',
        "Spam, phishing or scam attempts",
        "Mass marketing, promotions and newsletters",
        "Unsolicited sales pitches: companies offering us their products, materials, services, data lists or a supplier \"collaboration\"",
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
        new(SystemSettingKeys.DealerPhrases, "Inquiry detection", "Dealer / reseller phrases",
            "Phrases a business uses when it wants to resell, distribute, wholesale or partner with us. Each distinct match adds 2 points (max 4), "
            + "and a confident inquiry with any of them is labelled \"Dealer / Reseller\".", SettingValueType.RuleList, SystemSettingDefaults.DealerPhrases, 0, 9000),
        new(SystemSettingKeys.ProductTerms, "Inquiry detection", "Product terms",
            "Our product vocabulary in any language (sauna, heater, kiuas, Ofen, Saunova ...). Each distinct match adds 1 point (max 3). "
            + "SAWO model codes (NR-90NS, 1414RS ...) and kW ratings add 2 points automatically. Product terms alone never make an inquiry.",
            SettingValueType.RuleList, SystemSettingDefaults.ProductTerms, 0, 9000),
        new(SystemSettingKeys.RequestCues, "Inquiry detection", "Request cues",
            "Weak signs of a request (need, looking for, please, möchte ...). Each distinct match adds 1 point (max 2).",
            SettingValueType.RuleList, SystemSettingDefaults.RequestCues, 0, 9000),
        new(SystemSettingKeys.InquiryScoreThreshold, "Inquiry detection", "Score to accept as an inquiry without AI",
            "At or above this score, with no negative signal, the email is treated as an inquiry without calling the AI.", SettingValueType.Integer, "5", 1, 50, Unit: "points"),
        new(SystemSettingKeys.NotInquiryScoreThreshold, "Inquiry detection", "Score to reject without AI",
            "At or below minus this score the email is treated as not an inquiry without calling the AI. Anything in between goes to the AI.", SettingValueType.Integer, "3", 1, 50, Unit: "points"),
        new(SystemSettingKeys.SupervisionEnabled, "Inquiry supervision", "Supervise inquiries until resolved",
            "On: inquiries are followed through every stage until the agent confirms they are resolved, with reminders and management escalation. "
            + "Off: the older reminder and escalation engines are used, which stop at the first reply.", SettingValueType.Boolean, "true"),
        new(SystemSettingKeys.FirstResponseBusinessHours, "Inquiry supervision", "Time allowed for the first reply",
            "From the moment a customer's email arrives, how long the agent has to send the first reply before the reminders start.", SettingValueType.Integer, "4", 1, 240, Unit: "business hours"),
        new(SystemSettingKeys.ReminderIntervalBusinessHours, "Inquiry supervision", "Time between reminders",
            "The gap between the missed deadline and reminder 1, between each reminder, and between the last reminder and the management escalation.", SettingValueType.Integer, "2", 1, 240, Unit: "business hours"),
        new(SystemSettingKeys.RemindersBeforeEscalation, "Inquiry supervision", "Reminders before management is told",
            "How many reminders the agent gets first. 0 means no reminders: management is told one reminder interval after the deadline.", SettingValueType.Integer, "2", 0, 10, Unit: "reminders"),
        new(SystemSettingKeys.IdleBusinessDays, "Inquiry supervision", "Time allowed to answer a customer follow-up",
            "When the customer writes again, or a conversation stalls while the agent owes the next step, and nothing happens for this long, the same reminder and escalation steps start.", SettingValueType.Integer, "1", 1, 30, Unit: "business days"),
        new(SystemSettingKeys.CustomerWaitCalendarDays, "Inquiry supervision", "Wait for the customer's reply",
            "After the agent has replied, how long without an answer from the customer before the agent is told a follow-up may be needed. This never closes the inquiry.", SettingValueType.Integer, "3", 1, 60, Unit: "calendar days"),
        new(SystemSettingKeys.InternalWaitBusinessDays, "Inquiry supervision", "Wait for another department",
            "When the agent is waiting on a colleague or department and nothing happens for this long, the agent is asked for an update.", SettingValueType.Integer, "1", 1, 60, Unit: "business days"),
        new(SystemSettingKeys.ResolutionTargetBusinessDays, "Inquiry supervision", "Resolution target",
            "An inquiry still unresolved after this long is marked overdue. It is only a target: it never closes the inquiry.", SettingValueType.Integer, "5", 1, 120, Unit: "business days"),
        new(SystemSettingKeys.EscalationRepeatBusinessDays, "Inquiry supervision", "Move to the next escalation level after",
            "If management has been told and the inquiry is still neglected after this long, the next escalation level is notified.", SettingValueType.Integer, "1", 1, 60, Unit: "business days"),
        new(SystemSettingKeys.TestClockMinutes, "Inquiry supervision", "TEST MODE: count hours as minutes",
            "For trying the system out only. On: every supervision time above runs 60 times faster (1 hour = 1 minute, 1 day = 24 minutes) "
            + "and business hours, weekends and holidays are ignored. Turn it OFF before real use.", SettingValueType.Boolean, "false"),
        new(SystemSettingKeys.AiResolutionEnabled, "Inquiry supervision", "AI checks whether inquiries look resolved",
            "The AI reads the conversation and may mark an inquiry Potentially resolved. Only the agent can confirm it resolved.", SettingValueType.Boolean, "true"),
        new(SystemSettingKeys.AiResolutionMinConfidence, "Inquiry supervision", "AI confidence needed to suggest \"resolved\"",
            "The AI must be at least this confident before an inquiry is marked Potentially resolved.", SettingValueType.Integer, "75", 50, 100, Unit: "%"),
        new(SystemSettingKeys.AgentReplyRetentionDays, "Inquiry supervision", "Keep stored agent replies for",
            "The encrypted text of agent replies is erased this many days after the inquiry is closed. 0 keeps it.", SettingValueType.Integer, "365", 0, 36500, Unit: "days"),
        new(SystemSettingKeys.PasswordMinLength, "Security", "Minimum password length",
            "Enforced when creating users and changing or resetting passwords.", SettingValueType.Integer, "10", 8, 128, Unit: "characters"),
        new("retention.raw_email_content", "Retention", "Raw email content", RetentionDescription, SettingValueType.Integer, "0", 0, 36500, Unit: "days"),
        new("retention.email_metadata", "Retention", "Email metadata", RetentionDescription, SettingValueType.Integer, "0", 0, 36500, Unit: "days"),
        new("retention.cases", "Retention", "Cases", RetentionDescription, SettingValueType.Integer, "0", 0, 36500, Unit: "days"),
        new("retention.case_history", "Retention", "Case History", RetentionDescription, SettingValueType.Integer, "0", 0, 36500, Unit: "days"),
        new("retention.ai_results", "Retention", "AI results", RetentionDescription, SettingValueType.Integer, "0", 0, 36500, Unit: "days"),
        new("retention.employee_activity", "Retention", "Employee Activity", RetentionDescription, SettingValueType.Integer, "0", 0, 36500, Unit: "days"),
        new("retention.audit_logs", "Retention", "Audit Logs", RetentionDescription, SettingValueType.Integer, "0", 0, 36500, Unit: "days"),
        new("retention.agent_logs", "Retention", "Agent technical logs", RetentionDescription, SettingValueType.Integer, "0", 0, 36500, Unit: "days"),
        new("retention.outbound_email_logs", "Retention", "Outbound email logs", RetentionDescription, SettingValueType.Integer, "0", 0, 36500, Unit: "days"),
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
                d.Min, d.Max, row?.UpdatedByEmail, row?.UpdatedAt ?? row?.CreatedAt, d.Unit);
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
