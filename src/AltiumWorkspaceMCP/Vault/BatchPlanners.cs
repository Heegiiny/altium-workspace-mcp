namespace AltiumWorkspaceMCP.Vault;

/// <summary>A folder to be created: the GUID is assigned in advance so that children can reference parents.</summary>
/// <param name="Guid">GUID of the new folder.</param>
/// <param name="ParentGuid">GUID of the parent — an existing one or one created earlier in the same plan.</param>
/// <param name="InheritedTypeGuid">Type of the nearest existing ancestor folder (for type inheritance).</param>
public sealed record PlannedFolder(
    string Guid,
    string Name,
    string Path,
    string ParentGuid,
    string ParentPath,
    int Depth,
    string? InheritedTypeGuid);

/// <summary>Result of parsing a path list: what to create (parents before children), what already exists, what gets in the way.</summary>
public sealed record FolderCreationPlan(
    IReadOnlyList<PlannedFolder> ToCreate,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> Conflicts);

/// <summary>Parsing of a list of full folder paths for batch creation. Pure logic, no server.</summary>
public static class FolderCreationPlanner
{
    /// <summary>
    /// Missing parents are created before children, existing paths go into
    /// <see cref="FolderCreationPlan.Skipped"/>, repeats within the list collapse. Conflicts
    /// (an empty path, a non-existent root, a system name) are all listed at once.
    /// </summary>
    public static FolderCreationPlan Plan(IEnumerable<FolderNode> existing, IEnumerable<string> paths)
    {
        var known = new Dictionary<string, (string Guid, string? TypeGuid, string Path)>(StringComparer.OrdinalIgnoreCase);

        foreach (FolderNode node in existing)
        {
            known.TryAdd(Key(FolderResolver.Segments(node.Path)), (node.Guid, node.FolderTypeGuid, node.Path));
        }

        var toCreate = new List<PlannedFolder>();
        var skipped = new List<string>();
        var conflicts = new List<string>();
        var seenRequests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string request in paths)
        {
            string[] segments = FolderResolver.Segments(request ?? string.Empty);

            if (segments.Length == 0)
            {
                conflicts.Add("empty path");
                continue;
            }

            string fullKey = Key(segments);

            if (!seenRequests.Add(fullKey))
            {
                continue;
            }

            if (known.TryGetValue(fullKey, out var already))
            {
                skipped.Add(already.Path);
                continue;
            }

            if (!known.ContainsKey(Key(segments[..1])))
            {
                conflicts.Add($"'{string.Join('\\', segments)}': there is no root folder '{segments[0]}', this tool does not create roots");
                continue;
            }

            var pending = new List<PlannedFolder>();
            bool refused = false;
            (string Guid, string? TypeGuid, string Path) parent = known[Key(segments[..1])];
            string parentPath = segments[0];

            for (int index = 1; index < segments.Length; index++)
            {
                string[] prefix = segments[..(index + 1)];
                string prefixKey = Key(prefix);
                string prefixPath = string.Join('\\', prefix);

                if (known.TryGetValue(prefixKey, out var found))
                {
                    parent = found;
                    parentPath = prefixPath;
                    continue;
                }

                if (string.Equals(prefix[^1], FolderResolver.SystemFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    conflicts.Add($"'{prefixPath}': the name '{FolderResolver.SystemFolderName}' is taken by system folders, Altium creates them itself");
                    refused = true;
                    break;
                }

                var planned = new PlannedFolder(
                    System.Guid.NewGuid().ToString("D").ToUpperInvariant(),
                    prefix[^1],
                    prefixPath,
                    parent.Guid,
                    parentPath,
                    index,
                    parent.TypeGuid);

                pending.Add(planned);
                // The parent is needed by the next segment of the same path; it goes into the shared dictionary
                // only together with the whole path, if there was no conflict.
                known[prefixKey] = (planned.Guid, parent.TypeGuid, prefixPath);
                parent = (planned.Guid, parent.TypeGuid, prefixPath);
                parentPath = prefixPath;
            }

            if (refused)
            {
                // Roll back the record of the parents of the rejected path.
                foreach (PlannedFolder folder in pending)
                {
                    known.Remove(Key(FolderResolver.Segments(folder.Path)));
                }

                continue;
            }

            toCreate.AddRange(pending);
        }

        // Parents before children: by depth, within a level — the order of appearance.
        var ordered = toCreate
            .Select((folder, position) => (folder, position))
            .OrderBy(item => item.folder.Depth)
            .ThenBy(item => item.position)
            .Select(item => item.folder)
            .ToList();

        return new FolderCreationPlan(ordered, skipped, conflicts);
    }

    private static string Key(string[] segments) => string.Join('\\', segments);
}

/// <summary>An item offered for moving: what is known about it before writing.</summary>
public sealed record MoveCandidate(string ItemGuid, string Hrid, string CurrentFolderGuid, string CurrentFolderPath);

/// <summary>One move group: the target folder and items.</summary>
public sealed record MoveGroupInput(FolderNode Target, IReadOnlyList<MoveCandidate> Items);

/// <summary>What is moved into one target folder and what already lies there.</summary>
public sealed record MoveGroupPlan(FolderNode Target, IReadOnlyList<MoveCandidate> Moving, IReadOnlyList<string> AlreadyThere);

/// <summary>Result of parsing move groups: the plan by groups and the items that fell into two groups.</summary>
public sealed record MoveBatchPlan(IReadOnlyList<MoveGroupPlan> Groups, IReadOnlyList<string> Overlaps)
{
    public int MovingCount => Groups.Sum(group => group.Moving.Count);
}

/// <summary>Parsing of a batch move. Pure logic.</summary>
public static class MoveBatchPlanner
{
    public static MoveBatchPlan Plan(IReadOnlyList<MoveGroupInput> groups)
    {
        var firstGroup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var overlaps = new List<string>();
        var plans = new List<MoveGroupPlan>(groups.Count);

        for (int index = 0; index < groups.Count; index++)
        {
            MoveGroupInput group = groups[index];
            var moving = new List<MoveCandidate>();
            var already = new List<string>();
            var inGroup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (MoveCandidate item in group.Items)
            {
                if (!inGroup.Add(item.ItemGuid))
                {
                    continue;
                }

                if (firstGroup.TryGetValue(item.ItemGuid, out int other) && other != index)
                {
                    overlaps.Add($"{item.Hrid}: in groups {other + 1} and {index + 1}");
                    continue;
                }

                firstGroup[item.ItemGuid] = index;

                if (string.Equals(item.CurrentFolderGuid, group.Target.Guid, StringComparison.OrdinalIgnoreCase))
                {
                    already.Add(item.Hrid);
                }
                else
                {
                    moving.Add(item);
                }
            }

            plans.Add(new MoveGroupPlan(group.Target, moving, already));
        }

        return new MoveBatchPlan(plans, overlaps);
    }
}
