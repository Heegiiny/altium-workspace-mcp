namespace AltiumWorkspaceMCP.Vault;

/// <summary>How loosely a folder address is matched against the tree.</summary>
public enum FolderMatchMode
{
    /// <summary>
    /// GUID, full path, path ending by whole segments and a unique name.
    /// The mode for everything that determines where a write goes: an error in the address must not change the folder.
    /// </summary>
    Strict,

    /// <summary>
    /// The same plus loose matching of segments (<c>Passive\Resistors</c> →
    /// <c>Components\Passive Components\Resistors</c>). The mode for reading: the response says
    /// which folder was chosen.
    /// </summary>
    Lenient,
}

/// <summary>The found folder and the way it was found.</summary>
/// <param name="How">
/// <c>guid</c>, <c>path</c> (full path), <c>suffix</c> (path ending by segments),
/// <c>name</c> (unique name) or <c>fuzzy</c> (loosely, only <see cref="FolderMatchMode.Lenient"/>).
/// </param>
public sealed record FolderMatch(FolderNode Folder, string How)
{
    /// <summary>The address pointed to the folder unambiguously, there was no substitution.</summary>
    public bool IsExact => How is "guid" or "path";

    /// <summary>The <c>resolvedFolder</c> block for a read response; <see langword="null"/> if there was no substitution.</summary>
    public object? Describe(string requested) =>
        IsExact ? null : new { requested, path = Folder.Path, how = How };
}

/// <summary>The folder was not found; the text suggests similar ones and the next step.</summary>
public sealed class FolderNotFoundException(string message) : InvalidOperationException(message);

/// <summary>
/// Matching a folder address written by a person or an agent against the vault tree.
/// Pure logic: it does not call the server.
/// </summary>
public static class FolderResolver
{
    /// <summary>The name of the system folder; before <c>Attributes &amp; 1</c> appeared, being a system folder is determined by it.</summary>
    public const string SystemFolderName = "Datasheets";

    private const int MaxSuggestions = 5;
    private const int MaxAmbiguous = 10;
    private const int MaxTopLevel = 30;

    /// <summary>
    /// Finds a folder by address. The order: GUID → full path → path ending by whole
    /// segments → unique name → (only <see cref="FolderMatchMode.Lenient"/>) loose matching.
    /// </summary>
    /// <exception cref="FolderNotFoundException">There is no such folder; the text has similar ones.</exception>
    /// <exception cref="InvalidOperationException">Several folders match the address.</exception>
    public static FolderMatch Resolve(IEnumerable<FolderNode> folders, string request, FolderMatchMode mode)
    {
        var all = folders as IReadOnlyList<FolderNode> ?? folders.ToList();
        string[] wanted = Segments(request);

        if (wanted.Length == 0)
        {
            throw new ArgumentException(
                "No folder address given. The tree of the top levels is shown by vault_folders without parameters.");
        }

        // 1. GUID (case-insensitive).
        string trimmed = request.Trim();
        FolderNode? byGuid = all.FirstOrDefault(folder =>
            string.Equals(folder.Guid, trimmed, StringComparison.OrdinalIgnoreCase));

        if (byGuid is not null)
        {
            return new FolderMatch(byGuid, "guid");
        }

        // 2. Full path.
        var byPath = all.Where(folder => SameSegments(Segments(folder.Path), wanted)).ToList();

        if (byPath.Count == 1)
        {
            return new FolderMatch(byPath[0], "path");
        }

        if (byPath.Count > 1)
        {
            throw new InvalidOperationException(
                $"Several folders match the path '{string.Join('\\', wanted)}' — specify the GUID: "
                + string.Join("; ", byPath.Take(MaxAmbiguous).Select(folder => folder.Guid)) + ".");
        }

        // System folders take part further only if the request ends directly with their name:
        // "Datasheets" cannot be got loosely and by accident.
        bool systemRequested = IsSystemName(wanted[^1]);
        var pool = all.Where(folder => systemRequested || !IsSystem(folder)).ToList();

        // 3–4. Path ending by whole segments; for one segment this is a unique name.
        var bySuffix = pool.Where(folder => EndsWith(Segments(folder.Path), wanted)).ToList();

        if (bySuffix.Count == 1)
        {
            return new FolderMatch(bySuffix[0], wanted.Length == 1 ? "name" : "suffix");
        }

        if (bySuffix.Count > 1)
        {
            throw Ambiguous(string.Join('\\', wanted), bySuffix);
        }

        // 5. Loose matching — for reading only.
        if (mode == FolderMatchMode.Lenient)
        {
            var scored = pool
                .Select(folder => (Folder: folder, Score: Score(Segments(folder.Path), wanted)))
                .Where(item => item.Score.Matched == wanted.Length)
                .OrderByDescending(item => item.Score.Value)
                .ToList();

            if (scored.Count == 1
                || (scored.Count > 1 && scored[0].Score.Value > scored[1].Score.Value))
            {
                return new FolderMatch(scored[0].Folder, "fuzzy");
            }
        }

        throw NotFound(request, wanted, pool);
    }

    /// <summary>Address segments: the separators <c>/</c> and <c>\</c>, extra spaces and separators are removed.</summary>
    public static string[] Segments(string path) =>
        path.Replace('/', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => string.Join(' ', segment.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            .Where(segment => segment.Length > 0)
            .ToArray();

    /// <summary>
    /// A system folder: the system bit in <c>Attributes</c> or the name <see cref="SystemFolderName"/> —
    /// Datasheets folders created by earlier server versions without this bit are system ones too.
    /// </summary>
    public static bool IsSystem(FolderNode folder) => folder.IsSystem || IsSystemName(folder.Name);

    private static bool IsSystemName(string name) =>
        string.Equals(name, SystemFolderName, StringComparison.OrdinalIgnoreCase);

    private static bool SameSegments(string[] path, string[] wanted) =>
        path.Length == wanted.Length && EndsWith(path, wanted);

    private static bool EndsWith(string[] path, string[] wanted)
    {
        if (wanted.Length > path.Length)
        {
            return false;
        }

        for (int index = 0; index < wanted.Length; index++)
        {
            if (!string.Equals(path[path.Length - wanted.Length + index], wanted[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The score of a folder for a request: the request segments are matched in order against the path segments
    /// (a subsequence). The value is the share of matched request segments plus a bonus if
    /// the last request segment matched the name of the folder itself.
    /// </summary>
    internal static (int Matched, double Value) Score(string[] path, string[] wanted)
    {
        int matched = 0;
        int position = 0;
        bool lastMatchesLeaf = false;

        for (int query = 0; query < wanted.Length; query++)
        {
            for (int index = position; index < path.Length; index++)
            {
                if (SegmentMatches(wanted[query], path[index]))
                {
                    matched++;
                    position = index + 1;

                    if (query == wanted.Length - 1 && index == path.Length - 1)
                    {
                        lastMatchesLeaf = true;
                    }

                    break;
                }
            }
        }

        double value = (double)matched / wanted.Length + (lastMatchesLeaf ? 0.5 : 0);
        return (matched, value);
    }

    /// <summary>
    /// A request segment matched a path segment: equal case-insensitively, or the words of one
    /// (without plural endings) are entirely contained in the words of the other.
    /// </summary>
    private static bool SegmentMatches(string query, string segment)
    {
        if (string.Equals(query, segment, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var queryWords = Words(query);
        var segmentWords = Words(segment);

        return queryWords.Count > 0
            && segmentWords.Count > 0
            && (queryWords.IsSubsetOf(segmentWords) || segmentWords.IsSubsetOf(queryWords));
    }

    private static HashSet<string> Words(string text) =>
        text.Split(
                [' ', '-', '_', ',', '.', '(', ')', '&', '/', '\\'],
                StringSplitOptions.RemoveEmptyEntries)
            .Select(word => Stem(word.ToLowerInvariant()))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Strips a plural ending: resistors → resistor, switches → switch.</summary>
    private static string Stem(string word)
    {
        if (word.Length > 4 && (word.EndsWith("ches", StringComparison.Ordinal)
                || word.EndsWith("shes", StringComparison.Ordinal)
                || word.EndsWith("sses", StringComparison.Ordinal)
                || word.EndsWith("xes", StringComparison.Ordinal)))
        {
            return word[..^2];
        }

        return word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal)
            ? word[..^1]
            : word;
    }

    private static InvalidOperationException Ambiguous(string request, IReadOnlyList<FolderNode> candidates) =>
        new($"Several folders match the address '{request}' ({candidates.Count}): "
            + string.Join("; ", candidates.Select(folder => folder.Path).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Take(MaxAmbiguous))
            + (candidates.Count > MaxAmbiguous ? "; …" : string.Empty)
            + ". Specify the full path of one of them.");

    private static FolderNotFoundException NotFound(string request, string[] wanted, IReadOnlyList<FolderNode> pool)
    {
        var similar = pool
            .Select(folder => (Folder: folder, Score: Score(Segments(folder.Path), wanted).Value))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Folder.Path, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSuggestions)
            .Select(item => item.Folder.Path)
            .ToList();

        string hint = $"Specify the full path from the list or look for the folder: vault_folders nameContains=\"{wanted[^1]}\".";

        if (similar.Count > 0)
        {
            return new FolderNotFoundException(
                $"Folder '{request}' not found. Similar: {string.Join("; ", similar)}. {hint}");
        }

        var top = pool
            .Where(folder => Segments(folder.Path).Length <= 2)
            .Select(folder => folder.Path)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(MaxTopLevel)
            .ToList();

        return new FolderNotFoundException(
            $"Folder '{request}' not found, there are no similar ones. Top-level folders: {string.Join("; ", top)}. {hint}");
    }
}
