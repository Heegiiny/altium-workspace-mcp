using System.Diagnostics;
using AltiumWorkspaceMCP.Soap.Vault;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>A folder in the trash: the path restored from the chain of deleted parents.</summary>
public sealed record TrashFolderEntry(string Guid, string RestoredPath, DateTimeOffset DeletedAt);

/// <summary>
/// A selected trash item (before the usage count) — the input for the paged parse.
/// <paramref name="DeletedAt"/> — the deletion time of the nearest deleted ancestor folder, if the item
/// went to the trash with it (otherwise — the record's own edit time); <paramref name="DeletedWithFolderPath"/> is filled in the same case.
/// </summary>
public sealed record TrashItemCandidate(
    string Guid,
    string Hrid,
    string? ContentType,
    string RestoredFolderPath,
    DateTimeOffset DeletedAt,
    string? DeletedWithFolderPath);

/// <summary>A trash item with split usage: live parts and templates.</summary>
public sealed record TrashItemEntry(
    string Guid,
    string Hrid,
    string? ContentType,
    string RestoredFolderPath,
    DateTimeOffset DeletedAt,
    string? DeletedWithFolderPath,
    UsageSummary UsedByComponents,
    UsageSummary UsedByTemplates);

/// <summary>
/// What the trash list returned: a page and the total found by the selection (before <c>onlyUsed</c>,
/// which decides not the position but what to show). Folders are paged as a separate page
/// (<c>FoldersOffset</c>/<c>FoldersNextOffset</c>) — by the same technique as items, so that their
/// list also fits the response budget.
/// </summary>
public sealed record TrashListing(
    IReadOnlyList<TrashFolderEntry> Folders,
    int FoldersTotal,
    int FoldersOffset,
    int? FoldersNextOffset,
    IReadOnlyList<TrashItemEntry> Items,
    int ItemsTotal,
    int ItemsOffset,
    int? ItemsNextOffset,
    bool TimedOut);

/// <summary>A folder that can be restored — with a path for the parent-before-child sort.</summary>
public sealed record FolderToRestore(string Guid, string RestoredPath);

/// <summary>The place of an item in the trash: the path before deletion and when it was last edited.</summary>
public sealed record TrashItemLocation(string Guid, string Hrid, string RestoredPath, DateTimeOffset DeletedAt);

/// <summary>
/// Pure selection of trash items by path substring, deletion time window and content type —
/// done before the usage count.
/// </summary>
internal static class TrashSelection
{
    public static IReadOnlyList<TrashItemCandidate> SelectItems(
        IEnumerable<TrashItemCandidate> all,
        string? pathContains,
        DateTimeOffset? deletedAfter,
        DateTimeOffset? deletedBefore,
        IReadOnlyCollection<string>? contentTypes)
    {
        HashSet<string>? types = contentTypes is { Count: > 0 }
            ? new HashSet<string>(contentTypes, StringComparer.OrdinalIgnoreCase)
            : null;

        return all
            .Where(candidate => Matches(candidate.RestoredFolderPath, pathContains))
            .Where(candidate => deletedAfter is null || candidate.DeletedAt >= deletedAfter)
            .Where(candidate => deletedBefore is null || candidate.DeletedAt <= deletedBefore)
            .Where(candidate => types is null || (candidate.ContentType is { } type && types.Contains(type)))
            .OrderBy(candidate => candidate.RestoredFolderPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Hrid, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool Matches(string path, string? pathContains) =>
        string.IsNullOrWhiteSpace(pathContains) || path.Contains(pathContains, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Paged parse of already selected (see <see cref="TrashSelection"/>) and ordered
/// items. The same logic for a normal page (exactly <c>limit</c> items in a row from
/// <c>offset</c>) and <c>onlyUsed</c> (skips unused ones until it collects <c>limit</c>
/// shown or the list ends).
/// </summary>
internal static class TrashPageBuilder
{
    public sealed record Page(IReadOnlyList<TrashItemEntry> Items, int? NextOffset, bool TimedOut);

    /// <param name="usageLookup">
    /// The usage of an item or <see langword="null"/> if it could not be counted in time (the time
    /// budget for counting is exhausted). Such an answer stops the parse
    /// the same way as an exhausted <paramref name="limit"/> — but with <c>TimedOut = true</c> and
    /// <c>NextOffset</c> to continue from.
    /// </param>
    public static Page Build(
        IReadOnlyList<TrashItemCandidate> candidates,
        int offset,
        int limit,
        bool onlyUsed,
        Func<TrashItemCandidate, ItemUsage?> usageLookup)
    {
        var results = new List<TrashItemEntry>();
        int index = Math.Max(offset, 0);

        for (; index < candidates.Count; index++)
        {
            if (results.Count >= limit)
            {
                break;
            }

            ItemUsage? usage = usageLookup(candidates[index]);

            if (usage is null)
            {
                return new Page(results, index, TimedOut: true);
            }

            if (!onlyUsed || usage.Value.IsUsed)
            {
                TrashItemCandidate candidate = candidates[index];
                results.Add(new TrashItemEntry(
                    candidate.Guid,
                    candidate.Hrid,
                    candidate.ContentType,
                    candidate.RestoredFolderPath,
                    candidate.DeletedAt,
                    candidate.DeletedWithFolderPath,
                    usage.Value.Components,
                    usage.Value.Templates));
            }
        }

        int? nextOffset = index < candidates.Count ? index : null;
        return new Page(results, nextOffset, TimedOut: false);
    }
}

/// <summary>
/// The vault trash: what is in it, the path before deletion and the folder restore order.
/// Read-only and plan-making — the write itself is done by <see cref="VaultGateway"/>.
/// </summary>
public sealed class TrashService
{
    /// <summary>
    /// The threshold for choosing the usage count mode has been removed: the earlier batch walk read the whole vault at once and cost ~90 s
    /// regardless of the number of targets — more expensive than the per-item check at any count (15 items
    /// one by one — 9.5 s, 25 in a batch by the old scheme — 99 s). The rewritten
    /// <see cref="UsageService.FindExternalUsageBatchAsync"/> is a targeted batch: a few
    /// requests in batches for the WHOLE checked page at once, cheaper than 3–4 requests for each of its
    /// items one by one, starting from one or two items, and always cheaper than one by one with
    /// more — a separate threshold is no longer needed.
    /// </summary>
    private static readonly TimeSpan TimeBudget = TimeSpan.FromSeconds(60);

    private readonly VaultGateway _gateway;
    private readonly VaultCatalog _catalog;
    private readonly UsageService _usage;
    private readonly TemplateService _templates;

    public TrashService(VaultGateway gateway, VaultCatalog catalog, UsageService usage, TemplateService templates)
    {
        _gateway = gateway;
        _catalog = catalog;
        _usage = usage;
        _templates = templates;
    }

    /// <summary>
    /// Trash folders and items. <paramref name="offset"/>/<paramref name="limit"/> and the order
    /// (path, then HRID) — by items: pages do not overlap and do not lose items, the sum
    /// shown over all pages equals <c>ItemsTotal</c>.
    /// </summary>
    /// <param name="pathContains">Substring of the restored path; empty — the whole trash.</param>
    /// <param name="deletedAfter">Not earlier than this time (inclusive); <c>deletedAt</c> — the deletion time of the nearest deleted ancestor folder if the item went to the trash with it, otherwise — the last edit time of the record.</param>
    /// <param name="deletedBefore">Not later than this time (inclusive).</param>
    /// <param name="contentTypes">Content type HRIDs (for example <c>altium-symbol</c>); empty — any type.</param>
    /// <param name="onlyUsed">Show only items referenced by live parts or templates.</param>
    /// <param name="offset">The position in the selected (after filters, but before onlyUsed) item list to start from.</param>
    /// <param name="foldersOffset">The position in the folder list to start from — folders are paged separately from items.</param>
    /// <param name="limit">How many rows to show (folders and items — separately, by the same number).</param>
    public async Task<TrashListing> ListAsync(
        string? pathContains,
        DateTimeOffset? deletedAfter,
        DateTimeOffset? deletedBefore,
        IReadOnlyCollection<string>? contentTypes,
        bool onlyUsed,
        int offset,
        int foldersOffset,
        int limit,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        limit = Math.Max(limit, 1);
        offset = Math.Max(offset, 0);
        foldersOffset = Math.Max(foldersOffset, 0);

        var deletedFolders = await _gateway.GetFoldersAsync(
            options: VaultRequestOptions.Of(VaultRequestOptions.IncludeDeletedOnly, VaultRequestOptions.IncludeSystemFolders),
            limit: 100000,
            cancellationToken: cancellationToken);

        var deletedItems = await _gateway.GetItemsAsync(
            options: VaultRequestOptions.Of(VaultRequestOptions.IncludeDeletedOnly),
            limit: 100000,
            cancellationToken: cancellationToken);

        var nodes = await BuildNodesAsync(deletedFolders, cancellationToken);
        var pathByGuid = TrashPaths.BuildPathMap(nodes);
        var contentTypeCatalog = await _catalog.GetContentTypesAsync(cancellationToken);

        var deletedFolderTimestamps = deletedFolders
            .Where(folder => !string.IsNullOrEmpty(folder.GUID))
            .ToDictionary(
                folder => folder.GUID,
                folder => new DateTimeOffset(folder.LastModifiedAt, TimeSpan.Zero),
                StringComparer.OrdinalIgnoreCase);

        var folderEntriesAll = deletedFolders
            .Where(folder => !string.IsNullOrEmpty(folder.GUID))
            .Select(folder => new TrashFolderEntry(
                folder.GUID,
                pathByGuid.GetValueOrDefault(folder.GUID, folder.HRID ?? folder.GUID),
                new DateTimeOffset(folder.LastModifiedAt, TimeSpan.Zero)))
            .Where(entry => TrashSelection.Matches(entry.RestoredPath, pathContains))
            .Where(entry => deletedAfter is null || entry.DeletedAt >= deletedAfter)
            .Where(entry => deletedBefore is null || entry.DeletedAt <= deletedBefore)
            .OrderBy(entry => entry.RestoredPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Guid, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // An item whose folder (or any ancestor) is in the trash has an old LastModifiedAt, from
        // the last edit before deletion: the deletion time is carried only by the deleted folder itself.
        // Selection and sorting by deletedAt — already by the time restored from here.
        var candidates = deletedItems
            .Where(item => !string.IsNullOrEmpty(item.GUID))
            .Select(item =>
            {
                contentTypeCatalog.TryGetValue(item.ContentTypeGUID ?? string.Empty, out ALU_ContentType? contentType);
                string folderGuid = item.FolderGUID ?? string.Empty;

                TrashPaths.DeletedAncestor? ancestor = TrashPaths.FindNearestDeletedAncestor(
                    folderGuid, nodes, deletedFolderTimestamps, pathByGuid);

                return new TrashItemCandidate(
                    item.GUID,
                    item.HRID ?? item.GUID,
                    contentType?.HRID,
                    pathByGuid.GetValueOrDefault(folderGuid, folderGuid),
                    ancestor?.DeletedAt ?? new DateTimeOffset(item.LastModifiedAt, TimeSpan.Zero),
                    ancestor?.Path);
            });

        var selected = TrashSelection.SelectItems(candidates, pathContains, deletedAfter, deletedBefore, contentTypes);

        // What needs a usage check: a normal list — only the shown page
        // (at most limit), onlyUsed — the whole selected tail from offset, because unused
        // items do not count towards limit.
        IReadOnlyList<TrashItemCandidate> needUsage = onlyUsed
            ? selected.Skip(offset).ToList()
            : selected.Skip(offset).Take(limit).ToList();

        var templateReferences = await _templates.ListModelReferencesAsync(cancellationToken);
        var usageMap = await BuildUsageMapAsync(needUsage, templateReferences, clock, cancellationToken);

        TrashPageBuilder.Page page = TrashPageBuilder.Build(
            selected,
            offset,
            limit,
            onlyUsed,
            candidate => usageMap.TryGetValue(candidate.Guid, out ItemUsage usage) ? usage : null);

        var foldersPage = folderEntriesAll.Skip(foldersOffset).Take(limit).ToList();
        int? foldersNextOffset = foldersOffset + foldersPage.Count < folderEntriesAll.Count
            ? foldersOffset + foldersPage.Count
            : null;

        return new TrashListing(
            foldersPage,
            folderEntriesAll.Count,
            foldersOffset,
            foldersNextOffset,
            page.Items,
            selected.Count,
            offset,
            page.NextOffset,
            page.TimedOut);
    }

    /// <summary>
    /// The usage of each item of <paramref name="candidates"/> — by a targeted batch for the
    /// whole page at once (the earlier
    /// per-item threshold was removed, see <see cref="UsageService.FindExternalUsageBatchAsync"/>). The time
    /// budget (~60 s) is checked between the inner batches of the batch method; if not in time —
    /// the page items are simply absent from the result, and <see cref="TrashPageBuilder"/> treats this
    /// as the "not in time" boundary at the first of them.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, ItemUsage>> BuildUsageMapAsync(
        IReadOnlyList<TrashItemCandidate> candidates,
        IReadOnlyList<TemplateModelReference> templateReferences,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, ItemUsage>(StringComparer.OrdinalIgnoreCase);

        if (candidates.Count == 0 || clock.Elapsed >= TimeBudget)
        {
            return map;
        }

        UsageBatchResult batch = await _usage.FindExternalUsageBatchAsync(
            candidates.Select(candidate => candidate.Guid).ToList(),
            () => clock.Elapsed >= TimeBudget,
            cancellationToken);

        if (!batch.Completed)
        {
            return map;
        }

        foreach (TrashItemCandidate candidate in candidates)
        {
            var components = batch.Usages.TryGetValue(candidate.Guid, out IReadOnlyList<LiveUsage>? found) ? found : [];
            map[candidate.Guid] = BuildUsage(candidate.Guid, components, templateReferences);
        }

        return map;
    }

    private static ItemUsage BuildUsage(
        string itemGuid, IReadOnlyList<LiveUsage> components, IReadOnlyList<TemplateModelReference> templateReferences) =>
        new(
            UsageSummary.Of(components.Select(usage => usage.Hrid)),
            UsageSummary.Of(TemplateReferenceIndex.FindTemplateHrids(templateReferences, itemGuid)));

    /// <summary>
    /// Trash folders by their GUIDs, in restore order (parents before nested ones).
    /// A refusal if some GUID is not found in the trash.
    /// </summary>
    public async Task<IReadOnlyList<FolderToRestore>> OrderFoldersForRestoreAsync(
        IReadOnlyCollection<string> folderGuids,
        CancellationToken cancellationToken)
    {
        if (folderGuids.Count == 0)
        {
            return [];
        }

        var deletedFolders = await VaultGateway.ReadInChunksAsync(
            folderGuids,
            chunk => _gateway.GetFoldersAsync(
                VaultFilter.In("GUID", chunk),
                VaultRequestOptions.Of(VaultRequestOptions.IncludeDeletedOnly, VaultRequestOptions.IncludeSystemFolders),
                limit: 100000,
                cancellationToken: cancellationToken));

        var found = deletedFolders
            .Where(folder => !string.IsNullOrEmpty(folder.GUID))
            .ToDictionary(folder => folder.GUID, StringComparer.OrdinalIgnoreCase);

        var missing = folderGuids
            .Where(guid => !found.ContainsKey(guid))
            .ToList();

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Folders not found in the trash: {string.Join(", ", missing)}. "
                + "The trash list — vault_restore_items action=list.");
        }

        var nodes = await BuildNodesAsync(deletedFolders, cancellationToken);
        var pathByGuid = TrashPaths.BuildPathMap(nodes);

        return TrashPaths.OrderParentsFirst(folderGuids, pathByGuid)
            .Select(guid => new FolderToRestore(guid, pathByGuid.GetValueOrDefault(guid, guid)))
            .ToList();
    }

    /// <summary>
    /// Looks for the given GUIDs in the trash — without looking at each item separately:
    /// one read of all deleted folders (for the restored path) and one or two reads of items
    /// in batches by GUID. Those not found in the given set are simply absent from the result.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, TrashItemLocation>> FindItemsAsync(
        IReadOnlyCollection<string> itemGuids, CancellationToken cancellationToken)
    {
        if (itemGuids.Count == 0)
        {
            return new Dictionary<string, TrashItemLocation>(StringComparer.OrdinalIgnoreCase);
        }

        var deletedFolders = await _gateway.GetFoldersAsync(
            options: VaultRequestOptions.Of(VaultRequestOptions.IncludeDeletedOnly, VaultRequestOptions.IncludeSystemFolders),
            limit: 100000,
            cancellationToken: cancellationToken);

        var nodes = await BuildNodesAsync(deletedFolders, cancellationToken);
        var pathByGuid = TrashPaths.BuildPathMap(nodes);

        var deletedItems = await VaultGateway.ReadInChunksAsync(
            itemGuids,
            chunk => _gateway.GetItemsAsync(
                VaultFilter.In("GUID", chunk),
                VaultRequestOptions.Of(VaultRequestOptions.IncludeDeletedOnly),
                limit: 100000,
                cancellationToken: cancellationToken));

        var result = new Dictionary<string, TrashItemLocation>(StringComparer.OrdinalIgnoreCase);

        foreach (ALU_Item item in deletedItems)
        {
            if (string.IsNullOrEmpty(item.GUID))
            {
                continue;
            }

            result[item.GUID] = new TrashItemLocation(
                item.GUID,
                item.HRID ?? item.GUID,
                pathByGuid.GetValueOrDefault(item.FolderGUID ?? string.Empty, item.FolderGUID ?? string.Empty),
                new DateTimeOffset(item.LastModifiedAt, TimeSpan.Zero));
        }

        return result;
    }

    /// <summary>Path tree nodes: live folders from the catalog plus the passed deleted ones.</summary>
    private async Task<IReadOnlyDictionary<string, TrashNode>> BuildNodesAsync(
        IReadOnlyList<ALU_Folder> deletedFolders, CancellationToken cancellationToken)
    {
        var liveFolders = await _catalog.GetFoldersAsync(cancellationToken);
        var nodes = new Dictionary<string, TrashNode>(StringComparer.OrdinalIgnoreCase);

        foreach (FolderNode folder in liveFolders.Values)
        {
            nodes[folder.Guid] = new TrashNode(folder.Name, folder.ParentGuid);
        }

        foreach (ALU_Folder folder in deletedFolders)
        {
            if (string.IsNullOrEmpty(folder.GUID))
            {
                continue;
            }

            nodes[folder.GUID] = new TrashNode(
                folder.HRID ?? folder.GUID,
                string.IsNullOrEmpty(folder.ParentFolderGUID) ? null : folder.ParentFolderGUID);
        }

        return nodes;
    }
}
