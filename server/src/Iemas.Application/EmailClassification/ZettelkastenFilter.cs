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
    int NotInquiryThreshold)
{
    public static async Task<InquiryFilterSettings> LoadAsync(IAppDbContext db, CancellationToken cancellationToken) => new(
        IgnoredSenderList.Parse(await SystemSettingsService.GetValueAsync(db, SystemSettingKeys.InternalDomains, cancellationToken)),
        Lines(await SystemSettingsService.GetValueAsync(db, SystemSettingKeys.InquiryPhrases, cancellationToken)),
        Lines(await SystemSettingsService.GetValueAsync(db, SystemSettingKeys.NotInquiryPhrases, cancellationToken)),
        Lines(await SystemSettingsService.GetValueAsync(db, SystemSettingKeys.AutomatedSenderPatterns, cancellationToken)),
        await SystemSettingsService.GetIntAsync(db, SystemSettingKeys.InquiryScoreThreshold, cancellationToken),
        await SystemSettingsService.GetIntAsync(db, SystemSettingKeys.NotInquiryScoreThreshold, cancellationToken));

    public static List<string> Lines(string? value) =>
        (value ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 0).ToList();
}

public record InquiryFilterResult(InquiryFilterVerdict Verdict, int Score, IReadOnlyList<string> Signals, string Reason)
{
    /// <summary>One line for EmailClassification.DeterministicFilterReason and the CMS.</summary>
    public string Describe() =>
        $"Zettelkasten: {Verdict} (score {Score:+0;-0;0}). {Reason}{(Signals.Count == 0 ? "" : " Signals: " + string.Join("; ", Signals) + ".")}";
}

/// <summary>
/// Module 3 — the "Zettelkasten" screening stage that runs before any AI call. It scores an email
/// from explainable signals (phrases, sender patterns, internal domains, the classification
/// profile's include/exclude lists, and whether the sender is a known customer) and returns one of
/// three verdicts. Only <see cref="InquiryFilterVerdict.Uncertain"/> goes to the AI.
///
/// Context matters, so the filter is deliberately conservative:
/// - a confident Inquiry needs a high score AND no negative signal at all;
/// - any mix of positive and negative signals is Uncertain, never a confident decision;
/// - an internal sender is always NotInquiry for *new* inquiries (Q3). Internal email that belongs to
///   an existing inquiry's conversation is attached before this filter ever runs.
/// </summary>
public static class ZettelkastenFilter
{
    private const int InquiryPhrasePoints = 2;
    private const int InquiryPhraseCap = 6;
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

        var body = input.Body is { Length: > MaxBodyCharsScored } long_ ? long_[..MaxBodyCharsScored] : input.Body ?? string.Empty;
        var text = $"{input.Subject}\n{body}";
        var from = input.FromAddress.ToLowerInvariant();

        var positive = 0;
        var negative = 0;

        var inquiryHits = Matches(text, settings.InquiryPhrases);
        if (inquiryHits.Count > 0)
        {
            positive += Math.Min(inquiryHits.Count * InquiryPhrasePoints, InquiryPhraseCap);
            signals.Add($"inquiry phrase{(inquiryHits.Count > 1 ? "s" : "")} \"{string.Join("\", \"", inquiryHits.Take(4))}\"");
        }

        if (text.Contains('?'))
        {
            positive += QuestionPoints;
            signals.Add("asks a question");
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

        if (negative == 0 && score >= settings.InquiryThreshold)
        {
            return new InquiryFilterResult(InquiryFilterVerdict.Inquiry, score, signals,
                $"Strong inquiry signals with nothing against (threshold {settings.InquiryThreshold}).");
        }

        if (positive == 0 && score <= -settings.NotInquiryThreshold)
        {
            return new InquiryFilterResult(InquiryFilterVerdict.NotInquiry, score, signals,
                $"Only not-an-inquiry signals (threshold -{settings.NotInquiryThreshold}).");
        }

        // A strong negative can still outweigh weak positives decisively (e.g. a newsletter that
        // happens to ask "Interested?"): reject only when the negatives clearly dominate.
        if (score <= -settings.NotInquiryThreshold - 3)
        {
            return new InquiryFilterResult(InquiryFilterVerdict.NotInquiry, score, signals,
                "Not-an-inquiry signals clearly outweigh the rest.");
        }

        return new InquiryFilterResult(InquiryFilterVerdict.Uncertain, score, signals,
            positive > 0 && negative < 0
                ? "Mixed signals; the AI decides from context."
                : "Not enough signal either way; the AI decides from context.");
    }

    private static List<string> ZettelkastenLines(string? value) => InquiryFilterSettings.Lines(value);

    private static List<string> Matches(string text, IEnumerable<string> phrases) =>
        phrases.Where(p => p.Length > 0 && text.Contains(p, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
