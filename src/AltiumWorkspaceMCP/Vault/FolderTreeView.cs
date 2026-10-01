namespace AltiumWorkspaceMCP.Vault;

/// <summary>A row of the compact folder tree and what is known about it.</summary>
/// <param name="Folder">The folder.</param>
/// <param name="Hidden">How many nested folders are hidden by the listing depth (<c>[+N]</c>).</param>
/// <param name="Text">The ready row: <c>path  [+N]</c>, with GUID and description if they were requested.</param>
public sealed record FolderTreeLine(FolderNode Folder, int Hidden, string Text);

/// <summary>
/// A compact folder tree for <c>vault_folders</c>: rows instead of objects, limited
/// depth, selection by name. Pure logic without server calls.
/// </summary>
/// <remarks>
/// All vault folders with GUID and description (about five hundred) do not fit the response the
/// client accepts, so by default the top levels are returned, and what is hidden by depth
/// is marked with the number <c>[+N]</c>: the agent sees where to look next.
/// </remarks>
public static class FolderTreeView
{
    /// <summary>How many folder descriptions are printed: descriptions add noise to large listings.</summary>
    public const int DescriptionsLimit = 50;

    /// <summary>
    /// Row length in the JSON response: a backslash and a quote are doubled, and the folder path
    /// consists mostly of backslashes.
    /// </summary>
    public static int JsonLength(string text) =>
        text.Length + text.Count(symbol => symbol is '\\' or '"') + 4;

    /// <summary>The largest number of rows when selecting by name.</summary>
    public const int MaxNameMatches = 200;

    /// <summary>
    /// The tree down to the given depth. The level of the listing root is the first: without a root these are the top-level
    /// folders, with a root — the root itself.
    /// </summary>
    /// <param name="root">The listing root; <see langword="null"/> — the whole vault.</param>
    /// <param name="depth">How many levels to show (at least 1).</param>
    public static IReadOnlyList<FolderTreeLine> Levels(
        IEnumerable<FolderNode> all,
        FolderNode? root,
        int depth,
        bool includeSystem,
        bool includeGuids)
    {
        var scope = Scope(all, root, includeSystem);
        int rootSegments = root is null ? 0 : FolderResolver.Segments(root.Path).Length;
        int maxSegments = Math.Max(rootSegments - 1, 0) + Math.Max(depth, 1);

        var shown = scope
            .Select(item => (item.Folder, item.Segments))
            .Where(item => item.Segments.Length <= maxSegments)
            .ToList();

        var lines = new List<FolderTreeLine>(shown.Count);

        foreach (var (folder, segments) in shown)
        {
            // Hidden nested folders are counted only for the bottom level of the listing.
            int hidden = segments.Length == maxSegments
                ? scope.Count(other => other.Segments.Length > maxSegments && IsInside(other.Segments, segments))
                : 0;

            lines.Add(new FolderTreeLine(folder, hidden, Format(folder, hidden, includeGuids, description: false)));
        }

        return Sort(lines, includeGuids, describe: shown.Count <= DescriptionsLimit && root is not null);
    }

    /// <summary>Folders whose name contains the substring — at any depth, with full paths.</summary>
    public static IReadOnlyList<FolderTreeLine> ByName(
        IEnumerable<FolderNode> all,
        FolderNode? root,
        string nameContains,
        bool includeSystem,
        bool includeGuids)
    {
        var lines = Scope(all, root, includeSystem)
            .Where(item => item.Folder.Name.Contains(nameContains.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(item => new FolderTreeLine(item.Folder, 0, Format(item.Folder, 0, includeGuids, description: false)))
            .ToList();

        return Sort(lines, includeGuids, describe: lines.Count <= DescriptionsLimit && root is not null);
    }

    /// <summary>A folder row with a description, if there is one.</summary>
    private static IReadOnlyList<FolderTreeLine> Sort(List<FolderTreeLine> lines, bool includeGuids, bool describe)
    {
        var ordered = lines
            .OrderBy(line => line.Folder.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return describe
            ? ordered
                .Select(line => line with { Text = Format(line.Folder, line.Hidden, includeGuids, description: true) })
                .ToList()
            : ordered;
    }

    private static string Format(FolderNode folder, int hidden, bool includeGuids, bool description)
    {
        string text = folder.Path + (hidden > 0 ? $"  [+{hidden}]" : string.Empty);

        if (includeGuids)
        {
            text += " | " + folder.Guid;
        }

        return description && !string.IsNullOrWhiteSpace(folder.Description)
            ? $"{text} — {folder.Description.ReplaceLineEndings(" ").Trim()}"
            : text;
    }

    private static List<(FolderNode Folder, string[] Segments)> Scope(
        IEnumerable<FolderNode> all,
        FolderNode? root,
        bool includeSystem)
    {
        string[]? rootSegments = root is null ? null : FolderResolver.Segments(root.Path);

        return all
            .Where(folder => includeSystem || !FolderResolver.IsSystem(folder))
            .Select(folder => (Folder: folder, Segments: FolderResolver.Segments(folder.Path)))
            .Where(item => rootSegments is null || IsInside(item.Segments, rootSegments, orEqual: true))
            .ToList();
    }

    /// <summary>The path of <paramref name="child"/> lies strictly inside <paramref name="parent"/>.</summary>
    private static bool IsInside(string[] child, string[] parent, bool orEqual = false)
    {
        if (child.Length < parent.Length || (!orEqual && child.Length == parent.Length))
        {
            return false;
        }

        for (int index = 0; index < parent.Length; index++)
        {
            if (!string.Equals(child[index], parent[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
