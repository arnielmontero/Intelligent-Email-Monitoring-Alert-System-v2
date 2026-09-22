using Iemas.Domain.Ai;

namespace Iemas.Application.EmailClassification;

public record DeterministicFilterResult(bool ShouldSendToAi, string Reason);

/// <summary>
/// Requirements §27 — semantic classification must not rely on keywords alone, and the build
/// instructions require deterministic filtering to remain a distinct stage that AI *augments*,
/// not replaces. This stage is a cheap, explainable pre-check: does the subject/body contain any
/// signal from the profile's Include/Exclude lists at all?
///
/// This stage never makes the final relevance call by itself — it only decides whether the
/// (paid, slower) AI call is worth making, and gives the AI stage a documented reason for
/// investigation (§89). A message that matches nothing is still sent to the AI when the profile
/// defines no include list (fail-open, since an empty include list means "everything is in scope"
/// per §28's examples, which enumerate excludes as the sharper signal).
/// </summary>
public static class DeterministicEmailFilter
{
    public static DeterministicFilterResult Evaluate(string subject, string? body, ClassificationProfile? profile)
    {
        var haystack = $"{subject}\n{body}".ToLowerInvariant();

        if (profile is not null)
        {
            var excludeTerms = SplitLines(profile.ExcludeDefinitions);
            var matchedExclude = excludeTerms.FirstOrDefault(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
            if (matchedExclude is not null)
            {
                return new DeterministicFilterResult(false, $"Matched exclude term: \"{matchedExclude}\"");
            }

            var includeTerms = SplitLines(profile.IncludeDefinitions);
            if (includeTerms.Count > 0)
            {
                var matchedInclude = includeTerms.FirstOrDefault(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
                if (matchedInclude is not null)
                {
                    return new DeterministicFilterResult(true, $"Matched include term: \"{matchedInclude}\"");
                }

                // §27 — a superficial keyword match is not required for relevance, and the absence
                // of a keyword match is not proof of irrelevance either; semantic judgement is the
                // AI's job. The deterministic stage only short-circuits on a confident EXCLUDE
                // signal. Without an include match we still forward to AI, since subject/body
                // wording varies far more than any fixed keyword list can anticipate.
                return new DeterministicFilterResult(true, "No include/exclude term matched; deferring to AI semantic classification.");
            }
        }

        return new DeterministicFilterResult(true, profile is null
            ? "No classification profile configured; deferring to AI semantic classification."
            : "Profile defines no include list; deferring to AI semantic classification.");
    }

    private static List<string> SplitLines(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new List<string>();
        return value
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0)
            .ToList();
    }
}
