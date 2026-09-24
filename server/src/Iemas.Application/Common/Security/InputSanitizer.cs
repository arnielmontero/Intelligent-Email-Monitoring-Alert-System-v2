using System.Text.RegularExpressions;

namespace Iemas.Application.Common.Security;

/// <summary>
/// Server-side write-boundary sanitization for free-text fields (names, descriptions, etc.).
/// Rejects markup rather than encoding it — encoding at write time would corrupt the stored
/// value for any consumer that doesn't happen to decode it the same way the CMS does, whereas
/// rejecting outright keeps the stored value exactly what the caller sees. This exists as a
/// defense-in-depth write-side control; it does not assume or replace correct output encoding by
/// any given renderer (found live: the CMS currently escapes safely via React's default behavior,
/// but relying on that alone at the read side is not what "sanitize the write" means; see
/// IEMAS_Build_Progress_Tracker.md's Known Gaps entry this was written to close).
/// </summary>
public static class InputSanitizer
{
    private static readonly Regex HtmlMarkupPattern = new(@"[<>]", RegexOptions.Compiled);

    /// <summary>
    /// Returns null if the value is fine as-is, or an error message if it contains HTML-markup
    /// characters that free-text fields (names, labels, descriptions) have no legitimate need for.
    /// </summary>
    public static string? ValidateFreeText(string fieldLabel, string value)
    {
        return HtmlMarkupPattern.IsMatch(value)
            ? $"{fieldLabel} cannot contain '<' or '>' characters."
            : null;
    }
}
