namespace AltiumWorkspaceMCP.Vault;

/// <summary>
/// Parsing a record "identifier with a revision number after a hyphen", for example
/// <c>PCC-0009-3</c> — this is how the search index returns a footprint.
/// </summary>
/// <remarks>
/// The function checks nothing against the vault: it only separates the number after the last
/// hyphen. An identifier like <c>CMP-000-0960</c> is one identifier as a whole, not
/// <c>CMP-000</c> revision 960, so the calling code (<see cref="ComponentService.ResolveLinkTargetInfoAsync"/>)
/// first looks for an item with the full identifier and parses the record only on failure.
/// </remarks>
public static class LinkTargetId
{
    /// <summary>
    /// Separates the revision number appended after the last hyphen. Success does not mean that
    /// such an item or revision exists.
    /// </summary>
    public static bool TryParse(string target, out string baseId, out string revisionNumber)
    {
        baseId = string.Empty;
        revisionNumber = string.Empty;

        int dash = target.LastIndexOf('-');
        if (dash <= 0 || dash == target.Length - 1)
        {
            return false;
        }

        string suffix = target[(dash + 1)..];
        if (suffix.Length == 0 || !suffix.All(char.IsAsciiDigit))
        {
            return false;
        }

        baseId = target[..dash];
        revisionNumber = suffix;
        return true;
    }

    /// <summary>The target label for the preview: "PCC-000-0076 rev. 3", with a mark for a non-active revision.</summary>
    public static string DescribeTarget(string hrid, string? revisionId, bool isCurrent) =>
        string.IsNullOrEmpty(revisionId)
            ? hrid
            : $"{hrid} rev. {revisionId}" + (isCurrent ? string.Empty : " (not active)");

    /// <summary>
    /// The refusal text for a requested but non-existent revision number:
    /// "PCC-0009 has no revision 3; available: 1–2. Without a number the active one is taken."
    /// </summary>
    public static string DescribeMissingRevision(
        string hrid, string requestedRevision, IReadOnlyList<string> existingRevisions)
    {
        string available = existingRevisions.Count == 0
            ? "no revisions"
            : $"available: {FormatRevisionRanges(existingRevisions)}";

        return $"{hrid} has no revision {requestedRevision}; {available}. Without a number the active revision is taken.";
    }

    /// <summary>Numbers are folded into ranges (1, 2, 3 → "1–3"); non-numeric numbers — as a comma-separated list.</summary>
    private static string FormatRevisionRanges(IReadOnlyList<string> revisions)
    {
        var numeric = new List<int>();

        foreach (string revision in revisions)
        {
            if (!int.TryParse(revision, out int parsed))
            {
                return string.Join(", ", revisions);
            }

            numeric.Add(parsed);
        }

        numeric.Sort();

        var ranges = new List<string>();
        int start = numeric[0];
        int previous = numeric[0];

        foreach (int value in numeric.Skip(1))
        {
            if (value == previous + 1)
            {
                previous = value;
                continue;
            }

            ranges.Add(start == previous ? $"{start}" : $"{start}–{previous}");
            start = previous = value;
        }

        ranges.Add(start == previous ? $"{start}" : $"{start}–{previous}");
        return string.Join(", ", ranges);
    }
}
