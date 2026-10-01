using System.Text.RegularExpressions;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>
/// Typo hints: the nearest parameter names and neighboring component identifiers.
/// Pure logic without server calls.
/// </summary>
public static partial class NameSuggester
{
    /// <summary>An identifier like CMP-000-0946: letters, family number, sequence number.</summary>
    [GeneratedRegex(@"^(?<prefix>[A-Za-z]{2,5}-\d{1,4}-)(?<number>\d{1,8})$")]
    private static partial Regex IdentifierPattern();

    /// <summary>Levenshtein distance, case-insensitive.</summary>
    public static int Distance(string first, string second)
    {
        string a = first.ToLowerInvariant();
        string b = second.ToLowerInvariant();

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (int j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (int j = 1; j <= b.Length; j++)
            {
                int substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    /// <summary>
    /// Up to <paramref name="max"/> names nearest to <paramref name="name"/>. First the names that
    /// contain the requested one as a whole (or vice versa), then by Levenshtein distance;
    /// too distant ones (more than half the name length, but not fewer than three edits) are not offered.
    /// </summary>
    public static IReadOnlyList<string> Nearest(string name, IEnumerable<string> candidates, int max = 5)
    {
        string wanted = name.Trim();
        int threshold = Math.Max(3, wanted.Length / 2);

        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(candidate => !string.Equals(candidate, wanted, StringComparison.OrdinalIgnoreCase))
            .Select(candidate =>
            {
                // A name that contains the requested one as a whole ("manufacturer part" → "Manufacturer Part
                // Number") is better than a name that is contained in the requested one.
                int rank = candidate.Contains(wanted, StringComparison.OrdinalIgnoreCase) ? 0
                    : wanted.Contains(candidate, StringComparison.OrdinalIgnoreCase) ? 1
                    : 2;

                return (Candidate: candidate, Rank: rank, Distance: Distance(wanted, candidate));
            })
            .Where(item => item.Rank < 2 || item.Distance <= threshold)
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.Distance)
            .ThenBy(item => item.Candidate, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(item => item.Candidate)
            .ToList();
    }

    /// <summary>The string looks like a component identifier (<c>CMP-000-0946</c>).</summary>
    public static bool LooksLikeIdentifier(string value) => IdentifierPattern().IsMatch(value.Trim());

    /// <summary>
    /// The identifier prefix up to and including the last hyphen — neighbors are searched by it:
    /// <c>CMP-000-99999</c> → <c>CMP-000-</c>. Empty if the string does not look like an identifier.
    /// </summary>
    public static string? IdentifierPrefix(string value)
    {
        Match match = IdentifierPattern().Match(value.Trim());
        return match.Success ? match.Groups["prefix"].Value : null;
    }

    /// <summary>
    /// Up to <paramref name="max"/> identifiers with the same prefix, nearest in number to
    /// <paramref name="missing"/>; the smaller ones first, then the larger ones at an equal distance.
    /// </summary>
    public static IReadOnlyList<string> NearestByNumber(string missing, IEnumerable<string> candidates, int max = 5)
    {
        Match target = IdentifierPattern().Match(missing.Trim());
        if (!target.Success)
        {
            return [];
        }

        string prefix = target.Groups["prefix"].Value;
        long number = long.Parse(target.Groups["number"].Value, System.Globalization.CultureInfo.InvariantCulture);

        return candidates
            .Select(candidate => (Candidate: candidate, Match: IdentifierPattern().Match(candidate.Trim())))
            .Where(item => item.Match.Success
                && string.Equals(item.Match.Groups["prefix"].Value, prefix, StringComparison.OrdinalIgnoreCase))
            .Select(item => (
                item.Candidate,
                Number: long.Parse(item.Match.Groups["number"].Value, System.Globalization.CultureInfo.InvariantCulture)))
            .Where(item => item.Number != number)
            .OrderBy(item => Math.Abs(item.Number - number))
            .ThenBy(item => item.Number)
            .Take(max)
            .OrderBy(item => item.Number)
            .Select(item => item.Candidate)
            .ToList();
    }
}
