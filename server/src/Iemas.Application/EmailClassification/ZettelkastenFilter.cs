using System.Text.RegularExpressions;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Operations;
using Iemas.Domain.Ai;

namespace Iemas.Application.EmailClassification;

/// <summary>The three outcomes of the no-AI screening stage.</summary>
public enum InquiryFilterVerdict
{
    /// <summary>Confidently a customer inquiry — becomes a Case without an AI call.</summary>
    Inquiry = 0,

    /// <summary>Confidently not a new customer inquiry — stored, never a Case, no AI call.</summary>
    NotInquiry = 1,

    /// <summary>Signals are weak or contradictory — the AI decides from context.</summary>
    Uncertain = 2,
}

/// <summary>What kind of inquiry a confident <see cref="InquiryFilterVerdict.Inquiry"/> is.</summary>
public enum InquiryKind
{
    /// <summary>An end customer, installer or project asking about products, prices, parts or support.</summary>
    Customer = 0,

    /// <summary>A business that wants to resell, distribute, wholesale or partner with SAWO (dealer, reseller, OEM ...).</summary>
    Dealer = 1,
}

/// <summary>Everything the filter looks at. Pure data so the filter itself needs no database.</summary>
public record InquiryFilterInput(
    string Subject,
    string? Body,
    string FromAddress,
    /// <summary>True when the sender already had an inquiry (Case) that was not rejected as a wrong classification.</summary>
    bool SenderIsKnownCustomer,
    /// <summary>True when the sender is one of our own monitored mailboxes.</summary>
    bool SenderIsMonitoredMailbox);

public record InquiryFilterSettings(
    IgnoredSenderList InternalSenders,
    IReadOnlyList<string> InquiryPhrases,
    IReadOnlyList<string> NotInquiryPhrases,
    IReadOnlyList<string> AutomatedSenderPatterns,
    int InquiryThreshold,
    int NotInquiryThreshold,
    IReadOnlyList<string>? ProductTerms = null,
    IReadOnlyList<string>? RequestCues = null,
    IReadOnlyList<string>? DealerPhrases = null)
{
    public IReadOnlyList<string> ProductTermList => ProductTerms ?? [];
    public IReadOnlyList<string> RequestCueList => RequestCues ?? [];
    public IReadOnlyList<string> DealerPhraseList => DealerPhrases ?? [];

    public static async Task<InquiryFilterSettings> LoadAsync(IAppDbContext db, CancellationToken cancellationToken) => new(
        IgnoredSenderList.Parse(await SystemSettingsService.GetValueAsync(db, SystemSettingKeys.InternalDomains, cancellationToken)),
        Lines(await SystemSettingsService.GetValueAsync(db, SystemSettingKeys.InquiryPhrases, cancellationToken)),
        Lines(await SystemSettingsService.GetValueAsync(db, SystemSettingKeys.NotInquiryPhrases, cancellationToken)),
        Lines(await SystemSettingsService.GetValueAsync(db, SystemSettingKeys.AutomatedSenderPatterns, cancellationToken)),
        await SystemSettingsService.GetIntAsync(db, SystemSettingKeys.InquiryScoreThreshold, cancellationToken),
        await SystemSettingsService.GetIntAsync(db, SystemSettingKeys.NotInquiryScoreThreshold, cancellationToken),
        Lines(await SystemSettingsService.GetValueAsync(db, SystemSettingKeys.ProductTerms, cancellationToken)),
        Lines(await SystemSettingsService.GetValueAsync(db, SystemSettingKeys.RequestCues, cancellationToken)),
        Lines(await SystemSettingsService.GetValueAsync(db, SystemSettingKeys.DealerPhrases, cancellationToken)));

    /// <summary>The built-in defaults (what a fresh install uses); handy for tests and tooling.</summary>
    public static InquiryFilterSettings Defaults(string internalDomains = "sawo.com") => new(
        IgnoredSenderList.Parse(internalDomains),
        Lines(SystemSettingDefaults.InquiryPhrases),
        Lines(SystemSettingDefaults.NotInquiryPhrases),
        Lines(SystemSettingDefaults.AutomatedSenderPatterns),
        5, 3,
        Lines(SystemSettingDefaults.ProductTerms),
        Lines(SystemSettingDefaults.RequestCues),
        Lines(SystemSettingDefaults.DealerPhrases));

    public static List<string> Lines(string? value) =>
        (value ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 0).ToList();
}

public record InquiryFilterResult(InquiryFilterVerdict Verdict, int Score, IReadOnlyList<string> Signals, string Reason,
    InquiryKind Kind = InquiryKind.Customer)
{
    /// <summary>The Category recorded for a confident inquiry.</summary>
    public string Category => Kind == InquiryKind.Dealer ? DealerCategory : "Inquiry";

    public const string DealerCategory = "Dealer / Reseller";

    /// <summary>One line for EmailClassification.DeterministicFilterReason and the CMS.</summary>
    public string Describe() =>
        $"Zettelkasten: {Verdict}{(Verdict == InquiryFilterVerdict.Inquiry && Kind == InquiryKind.Dealer ? " (dealer / reseller)" : "")} (score {Score:+0;-0;0}). {Reason}{(Signals.Count == 0 ? "" : " Signals: " + string.Join("; ", Signals) + ".")}";
}

/// <summary>
/// Module 3 — the "Zettelkasten" screening stage that runs before any AI call. It scores an email
/// from explainable signals and returns one of three verdicts. Only <see cref="InquiryFilterVerdict.Uncertain"/> goes to the AI.
///
/// Signals (tuned on real SAWO website/info@ inquiries, see inquiries_export.csv):
/// - <b>intent</b>: inquiry phrases (quote, price, where to buy, spare part ...), dealer/reseller phrases,
///   weak request cues (need, looking for, please, möchte, haluaisin ...) and a question mark;
/// - <b>product context</b>: SAWO/sauna vocabulary in many languages, SAWO model codes (NR-90NS, 1414RS,
///   STN-120-3 ...) and kW ratings — real inquiries are often one short line, so naming a product is strong evidence;
/// - <b>against</b>: newsletter/notice/auto-reply phrases, cold vendor pitches, automated senders.
///
/// Context still matters, so the filter stays conservative:
/// - a confident Inquiry needs a high score, at least one intent signal AND no negative signal at all;
/// - any mix of positive and negative signals is Uncertain unless the negatives clearly dominate;
/// - internal senders and test messages are always NotInquiry for *new* inquiries. Internal email that
///   belongs to an existing inquiry's conversation is attached before this filter ever runs.
/// </summary>
public static class ZettelkastenFilter
{
    private const int InquiryPhrasePoints = 2;
    private const int InquiryPhraseCap = 6;
    private const int DealerPhrasePoints = 2;
    private const int DealerPhraseCap = 4;
    private const int RequestCuePoints = 1;
    private const int RequestCueCap = 2;
    private const int ProductTermPoints = 1;
    private const int ProductTermCap = 3;
    private const int ModelCodePoints = 2;
    private const int NotInquiryPhrasePoints = -3;
    private const int NotInquiryPhraseCap = -9;
    private const int AutomatedSenderPoints = -4;
    private const int ProfileExcludePoints = -5;
    private const int ProfileIncludePoints = 1;
    private const int ProfileIncludeCap = 2;
    private const int QuestionPoints = 1;
    private const int KnownCustomerPoints = 2;

    /// <summary>Quoted history further down a reply often repeats old content; only the start of the body is scored.</summary>
    private const int MaxBodyCharsScored = 6000;

    /// <summary>
    /// SAWO article / model codes as customers write them: NR-90NS-N, SCA-80NSUK, ARI3-90Ni, STN-120-3-DFP-V2,
    /// TH9-180NS-P, SAU-UI-V2-P, INC-S, 1414RS, 1920MRL, 1111-IR-D, 743-4SCP-R, 449-D, HP06-002, SP02-200 ... and kW ratings (6 kW, 4,5kw).
    /// Case-sensitive on the letters so ordinary words never match; ISO-/COVID- style tokens are excluded.
    /// </summary>
    private static readonly Regex ModelCode = new(
        @"\b(?!ISO|COVID|ROHS|ISBN)(?:[A-Z]{2,5}\d{0,2}-(?:\d{2,3}|[A-Z]{1,3}\b)[A-Za-z0-9-]*" +
        @"|\d{4}(?:RS|RL|LS|LL|MS|MD|MRL|RD)\b|\d{4}-IR\b" +
        @"|\d{3}-(?=[A-Z0-9]*[A-Z])[A-Z0-9]{1,5}\b" +
        @"|[A-Z]{2}\d{2}-\d{3}\b)" +
        @"|\b\d{1,2}(?:[.,]\d{1,2})?\s?(?:kw|kW|KW|Kw)\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static InquiryFilterResult Evaluate(InquiryFilterInput input, InquiryFilterSettings settings, ClassificationProfile? profile)
    {
        var signals = new List<string>();

        if (input.SenderIsMonitoredMailbox)
        {
            return new InquiryFilterResult(InquiryFilterVerdict.NotInquiry, 0, signals,
                "Sender is one of our own monitored mailboxes; internal email never starts a new customer inquiry.");
        }

        if (settings.InternalSenders.Matches(input.FromAddress))
        {
            return new InquiryFilterResult(InquiryFilterVerdict.NotInquiry, 0, signals,
                $"Sender {input.FromAddress} is in an internal company domain; internal email never starts a new customer inquiry.");
        }

        if (IsTestMessage(input.Subject, input.Body))
        {
            signals.Add("test message");
            return new InquiryFilterResult(InquiryFilterVerdict.NotInquiry, 0, signals,
                "The message is only a test (\"test\", \"testing\" ...); web-form and mailbox tests never become inquiries.");
        }

        var body = input.Body is { Length: > MaxBodyCharsScored } long_ ? long_[..MaxBodyCharsScored] : input.Body ?? string.Empty;
        var text = $"{input.Subject}\n{body}";
        var from = input.FromAddress.ToLowerInvariant();

        var positive = 0;
        var negative = 0;
        var hasIntent = false;

        var inquiryHits = Matches(text, settings.InquiryPhrases);
        if (inquiryHits.Count > 0)
        {
            positive += Math.Min(inquiryHits.Count * InquiryPhrasePoints, InquiryPhraseCap);
            hasIntent = true;
            signals.Add($"inquiry phrase{(inquiryHits.Count > 1 ? "s" : "")} \"{string.Join("\", \"", inquiryHits.Take(4))}\"");
        }

        var dealerHits = Matches(text, settings.DealerPhraseList);
        if (dealerHits.Count > 0)
        {
            positive += Math.Min(dealerHits.Count * DealerPhrasePoints, DealerPhraseCap);
            hasIntent = true;
            signals.Add($"dealer/reseller phrase{(dealerHits.Count > 1 ? "s" : "")} \"{string.Join("\", \"", dealerHits.Take(4))}\"");
        }

        var cueHits = Matches(text, settings.RequestCueList);
        if (cueHits.Count > 0)
        {
            positive += Math.Min(cueHits.Count * RequestCuePoints, RequestCueCap);
            hasIntent = true;
            signals.Add($"request cue{(cueHits.Count > 1 ? "s" : "")} \"{string.Join("\", \"", cueHits.Take(3))}\"");
        }

        if (text.Contains('?') || text.Contains('？'))
        {
            positive += QuestionPoints;
            hasIntent = true;
            signals.Add("asks a question");
        }

        var productHits = Matches(text, settings.ProductTermList);
        if (productHits.Count > 0)
        {
            positive += Math.Min(productHits.Count * ProductTermPoints, ProductTermCap);
            signals.Add($"product term{(productHits.Count > 1 ? "s" : "")} \"{string.Join("\", \"", productHits.Take(3))}\"");
        }

        var modelCode = ModelCode.Match(text);
        if (modelCode.Success)
        {
            positive += ModelCodePoints;
            signals.Add($"model code / rating \"{modelCode.Value.Trim()}\"");
        }

        if (input.SenderIsKnownCustomer)
        {
            positive += KnownCustomerPoints;
            signals.Add("sender has made inquiries before");
        }

        if (profile is not null)
        {
            var includeHits = Matches(text, ZettelkastenLines(profile.IncludeDefinitions));
            if (includeHits.Count > 0)
            {
                positive += Math.Min(includeHits.Count * ProfileIncludePoints, ProfileIncludeCap);
                signals.Add($"profile include \"{includeHits[0]}\"");
            }

            // An administrator's explicit exclude rule stays decisive, exactly as before this filter existed.
            var excludeHits = Matches(text, ZettelkastenLines(profile.ExcludeDefinitions));
            if (excludeHits.Count > 0)
            {
                signals.Add($"profile exclude \"{excludeHits[0]}\"");
                return new InquiryFilterResult(InquiryFilterVerdict.NotInquiry, ProfileExcludePoints, signals,
                    $"Matched exclude term: \"{excludeHits[0]}\" (classification profile rule).");
            }
        }

        var notInquiryHits = Matches(text, settings.NotInquiryPhrases);
        if (notInquiryHits.Count > 0)
        {
            negative += Math.Max(notInquiryHits.Count * NotInquiryPhrasePoints, NotInquiryPhraseCap);
            signals.Add($"not-inquiry phrase{(notInquiryHits.Count > 1 ? "s" : "")} \"{string.Join("\", \"", notInquiryHits.Take(4))}\"");
        }

        var automated = settings.AutomatedSenderPatterns.FirstOrDefault(p => from.Contains(p, StringComparison.OrdinalIgnoreCase));
        if (automated is not null)
        {
            negative += AutomatedSenderPoints;
            signals.Add($"automated sender (\"{automated}\")");
        }

        var score = positive + negative;

        // Naming one of our products or a model code is the strongest sign of a genuine buyer: in the real
        // inquiry data, every product-related message with a request cue and nothing against it was an inquiry.
        // Product context therefore lowers the bar by one point (never below 3).
        var hasProductContext = productHits.Count > 0 || modelCode.Success;
        var threshold = hasProductContext ? Math.Max(3, settings.InquiryThreshold - 1) : settings.InquiryThreshold;

        if (negative == 0 && hasIntent && score >= threshold)
        {
            var kind = dealerHits.Count > 0 ? InquiryKind.Dealer : InquiryKind.Customer;
            return new InquiryFilterResult(InquiryFilterVerdict.Inquiry, score, signals,
                kind == InquiryKind.Dealer
                    ? $"Business asking to resell, distribute or partner, with nothing against (threshold {threshold})."
                    : $"Strong inquiry signals with nothing against (threshold {threshold}{(hasProductContext ? ", lowered by product context" : "")}).",
                kind);
        }

        if (positive == 0 && score <= -settings.NotInquiryThreshold)
        {
            return new InquiryFilterResult(InquiryFilterVerdict.NotInquiry, score, signals,
                $"Only not-an-inquiry signals (threshold -{settings.NotInquiryThreshold}).");
        }

        // A strong negative can still outweigh weak positives decisively (e.g. a list vendor that
        // happens to ask "Interested?" and mention pricing): reject when the negatives clearly dominate.
        if (score <= -settings.NotInquiryThreshold - 3 || (negative <= 2 * NotInquiryPhrasePoints && score <= 0))
        {
            return new InquiryFilterResult(InquiryFilterVerdict.NotInquiry, score, signals,
                "Not-an-inquiry signals clearly outweigh the rest.");
        }

        return new InquiryFilterResult(InquiryFilterVerdict.Uncertain, score, signals,
            positive > 0 && negative < 0
                ? "Mixed signals; the AI decides from context."
                : "Not enough signal either way; the AI decides from context.");
    }

    private static readonly HashSet<string> TestWords = new(StringComparer.OrdinalIgnoreCase) { "test", "testing", "teest", "tst", "tested" };

    private static readonly HashSet<string> TestFillerWords = new(StringComparer.OrdinalIgnoreCase)
        { "hello", "hi", "just", "this", "is", "a", "only", "message", "email", "mail", "subject", "ok", "please", "ignore" };

    private static readonly string[] TestSentences = ["does this work", "is this working", "does it work", "is it working"];

    /// <summary>
    /// True when the whole body (or the subject, if the body is empty) is just a few words that are all
    /// "test"-style filler, e.g. "Test", "TEEST", "hello test", "Just Testing", "Does this work?". Word-based on
    /// purpose: a "test" phrase would also match "latest" or "contest".
    /// </summary>
    private static bool IsTestMessage(string subject, string? body)
    {
        var source = string.IsNullOrWhiteSpace(body) ? subject : body;
        var words = Regex.Matches(source ?? string.Empty, @"\p{L}+").Select(m => m.Value).ToList();
        if (words.Count is 0 or > 5) return false;
        if (TestSentences.Contains(string.Join(' ', words).ToLowerInvariant())) return true;
        return words.Any(TestWords.Contains) && words.All(w => TestWords.Contains(w) || TestFillerWords.Contains(w));
    }

    private static List<string> ZettelkastenLines(string? value) => InquiryFilterSettings.Lines(value);

    private static List<string> Matches(string text, IEnumerable<string> phrases) =>
        phrases.Where(p => p.Length > 0 && text.Contains(p, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
