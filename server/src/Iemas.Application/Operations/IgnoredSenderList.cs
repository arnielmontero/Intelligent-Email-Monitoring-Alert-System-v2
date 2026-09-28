using System.Text.RegularExpressions;
using Iemas.Application.Common.Interfaces;

namespace Iemas.Application.Operations;

/// <summary>
/// The "Ignored senders" system setting: email from these domains or addresses is stored but
/// never classified, never becomes a Case and never notifies anyone. A domain entry also covers
/// its subdomains (sawo.com matches mail.sawo.com).
/// </summary>
public sealed class IgnoredSenderList
{
    private static readonly Regex DomainPattern = new(@"^(?=.{3,253}$)([a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$", RegexOptions.Compiled);
    private static readonly Regex AddressPattern = new(@"^[^\s@]{1,64}@([^\s@]+)$", RegexOptions.Compiled);

    private readonly HashSet<string> _domains;
    private readonly HashSet<string> _addresses;

    private IgnoredSenderList(HashSet<string> domains, HashSet<string> addresses)
    {
        _domains = domains;
        _addresses = addresses;
    }

    public static readonly IgnoredSenderList Empty = new(new HashSet<string>(), new HashSet<string>());

    public bool IsEmpty => _domains.Count == 0 && _addresses.Count == 0;

    public static async Task<IgnoredSenderList> LoadAsync(IAppDbContext db, CancellationToken cancellationToken) =>
        Parse(await SystemSettingsService.GetValueAsync(db, SystemSettingKeys.IgnoredSenders, cancellationToken));

    /// <summary>Parses stored (already validated) entries; anything unrecognisable is ignored.</summary>
    public static IgnoredSenderList Parse(string? stored)
    {
        var domains = new HashSet<string>();
        var addresses = new HashSet<string>();
        foreach (var entry in SplitEntries(stored))
        {
            if (entry.Contains('@')) addresses.Add(entry);
            else domains.Add(entry);
        }
        return new IgnoredSenderList(domains, addresses);
    }

    /// <summary>Normalises user input into one sorted entry per line, or returns an error naming the bad entry.</summary>
    public static (string? Normalized, string? Error) Normalize(string? input)
    {
        var entries = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in SplitEntries(input))
        {
            if (entry.Contains('@'))
            {
                var match = AddressPattern.Match(entry);
                if (!match.Success || !DomainPattern.IsMatch(match.Groups[1].Value))
                {
                    return (null, $"\"{entry}\" is not a valid email address.");
                }
            }
            else if (!DomainPattern.IsMatch(entry))
            {
                return (null, $"\"{entry}\" is not a valid domain (for example: sawo.com).");
            }
            entries.Add(entry);
        }
        return (string.Join('\n', entries), null);
    }

    public bool Matches(string? senderAddress)
    {
        if (IsEmpty || string.IsNullOrWhiteSpace(senderAddress)) return false;
        var address = senderAddress.Trim().ToLowerInvariant();
        if (_addresses.Contains(address)) return true;

        var at = address.LastIndexOf('@');
        if (at < 0) return false;
        var domain = address[(at + 1)..];
        while (true)
        {
            if (_domains.Contains(domain)) return true;
            var dot = domain.IndexOf('.');
            if (dot < 0) return false;
            domain = domain[(dot + 1)..];
        }
    }

    /// <summary>Entries may be separated by new lines, commas, semicolons or spaces; a leading "@" or "*." is accepted.</summary>
    private static IEnumerable<string> SplitEntries(string? value) =>
        (value ?? string.Empty)
            .Split(new[] { '\n', '\r', ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim().ToLowerInvariant())
            .Select(e => e.StartsWith("*.") ? e[2..] : e.StartsWith('@') ? e[1..] : e)
            .Where(e => e.Length > 0);
}
