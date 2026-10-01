namespace AltiumWorkspaceMCP.Vault;

/// <summary>The name of a folder tree node and the GUID of its parent (or null — the root).</summary>
public readonly record struct TrashNode(string Hrid, string? ParentGuid);

/// <summary>
/// Pure logic for restoring paths and the restore order by the folder tree
/// assembled from live and deleted nodes together: a deleted folder knows its
/// parent even if the parent itself is in the trash too.
/// </summary>
public static class TrashPaths
{
    /// <summary>The node path, climbing to the root along the parent references.</summary>
    public static string PathOf(string guid, IReadOnlyDictionary<string, TrashNode> nodes)
    {
        var segments = new List<string>();
        string? current = guid;
        int guard = 0;

        while (current is not null && guard++ < 64 && nodes.TryGetValue(current, out TrashNode node))
        {
            segments.Add(node.Hrid);
            current = node.ParentGuid;
        }

        segments.Reverse();
        return string.Join('\\', segments);
    }

    /// <summary>The path for every node of the dictionary.</summary>
    public static IReadOnlyDictionary<string, string> BuildPathMap(IReadOnlyDictionary<string, TrashNode> nodes)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string guid in nodes.Keys)
        {
            result[guid] = PathOf(guid, nodes);
        }

        return result;
    }

    /// <summary>The nearest deleted ancestor folder: its GUID, restored path and deletion time.</summary>
    public readonly record struct DeletedAncestor(string Guid, string Path, DateTimeOffset DeletedAt);

    /// <summary>
    /// The nearest deleted folder on the way from <paramref name="startGuid"/> to the root, including
    /// <paramref name="startGuid"/> itself: an item
    /// whose immediate folder is deleted went to the trash with it, even if its parent
    /// is alive; if both the folder and its ancestor are deleted — the one that directly contains the item is nearer.
    /// <see langword="null"/> — neither the folder itself nor any ancestor is in the trash (the item was deleted
    /// separately, the deletion time is its own).
    /// </summary>
    public static DeletedAncestor? FindNearestDeletedAncestor(
        string startGuid,
        IReadOnlyDictionary<string, TrashNode> nodes,
        IReadOnlyDictionary<string, DateTimeOffset> deletedFolderTimestamps,
        IReadOnlyDictionary<string, string> pathByGuid)
    {
        string? current = startGuid;
        int guard = 0;

        while (current is not null && guard++ < 64)
        {
            if (deletedFolderTimestamps.TryGetValue(current, out DateTimeOffset deletedAt))
            {
                return new DeletedAncestor(current, pathByGuid.GetValueOrDefault(current, current), deletedAt);
            }

            current = nodes.TryGetValue(current, out TrashNode node) ? node.ParentGuid : null;
        }

        return null;
    }

    /// <summary>
    /// The folder restore order: parents before children — by path depth, at equal
    /// depth alphabetically (a deterministic order for folders of the same depth).
    /// </summary>
    public static IReadOnlyList<string> OrderParentsFirst(
        IEnumerable<string> guids, IReadOnlyDictionary<string, string> pathByGuid) =>
        guids
            .OrderBy(guid => Depth(pathByGuid.GetValueOrDefault(guid, guid)))
            .ThenBy(guid => pathByGuid.GetValueOrDefault(guid, guid), StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static int Depth(string path) => path.Count(character => character == '\\');
}
