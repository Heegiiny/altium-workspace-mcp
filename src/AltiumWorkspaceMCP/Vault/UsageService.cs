using AltiumWorkspaceMCP.Soap.Vault;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>A live part whose active revision references the object being checked.</summary>
public sealed record LiveUsage(string Hrid, string FolderPath);

/// <summary>Result of the usage of one item: how many reference it and the first (at most three) identifiers.</summary>
public sealed record UsageSummary(int Count, IReadOnlyList<string> SampleHrids)
{
    public static readonly UsageSummary Empty = new(0, []);

    public static UsageSummary Of(IEnumerable<string> hrids)
    {
        var ordered = hrids
            .Where(hrid => !string.IsNullOrEmpty(hrid))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(hrid => hrid, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return ordered.Count == 0 ? Empty : new UsageSummary(ordered.Count, ordered.Take(3).ToList());
    }
}

/// <summary>
/// Live parts and templates that reference an item — separately (the trash row
/// shows both).
/// </summary>
public readonly record struct ItemUsage(UsageSummary Components, UsageSummary Templates)
{
    public bool IsUsed => Components.Count > 0 || Templates.Count > 0;
}

/// <summary>
/// Result of the batch check (<see cref="UsageService.FindExternalUsageBatchAsync"/>):
/// <paramref name="Completed"/> = <see langword="false"/> — the time budget ran out midway,
/// <paramref name="Usages"/> is empty in this case (it cannot be finished partially, see the method description).
/// </summary>
public sealed record UsageBatchResult(
    IReadOnlyDictionary<string, IReadOnlyList<LiveUsage>> Usages, bool Completed);

/// <summary>
/// Determines whether live parts reference an item in the vault by their active revision.
/// Needed before deletion: a footprint or symbol that is still in use
/// must not be deleted, even with force.
/// </summary>
/// <remarks>
/// The per-item check (<see cref="FindExternalUsageAsync"/>) uses <c>GetWhereUsedByItem</c> —
/// it is much cheaper than reading the links of all live parts of the vault if there is one item. The reverse
/// selection (which revision is active) is done on the already found candidates through
/// <see cref="ComponentService.ReadByIdsAsync"/> — the same path as everywhere in the project.
/// The mass check (<see cref="FindExternalUsageBatchAsync"/>) goes the same way, but in a batch for all checked items at once:
/// their revisions (including deleted ones — the items themselves are already in the trash), links to these revisions, the revisions
/// of the parents and whether it is active — without reading the whole vault. The cost grows with the number
/// of checked items, not with the database size (the old variant read all live items and the links
/// of all their revisions at once — more expensive than the per-item check at any number of targets, ~90 s on the live
/// server regardless of whether 15 or 25 items are checked).
/// </remarks>
public sealed class UsageService
{
    private readonly VaultGateway _gateway;
    private readonly ComponentService _components;

    public UsageService(VaultGateway gateway, ComponentService components)
    {
        _gateway = gateway;
        _components = components;
    }

    /// <summary>
    /// Live parts outside <paramref name="excludeItemGuids"/> whose active revision
    /// references <paramref name="itemGuid"/> by any link role.
    /// </summary>
    public async Task<IReadOnlyList<LiveUsage>> FindExternalUsageAsync(
        string itemGuid,
        IReadOnlyCollection<string> excludeItemGuids,
        CancellationToken cancellationToken)
    {
        WhereUsedResult usage = await _gateway.GetWhereUsedAllAsync(itemGuid, cancellationToken);

        if (usage.Relations.Count == 0)
        {
            return [];
        }

        var revisionGuids = usage.Relations
            .Select(relation => relation.ParentRevisionGuid)
            .Where(guid => !string.IsNullOrEmpty(guid))
            .Select(guid => guid!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (revisionGuids.Count == 0)
        {
            return [];
        }

        var revisions = await VaultGateway.ReadInChunksAsync(
            revisionGuids,
            chunk => _gateway.GetItemRevisionsAsync(
                VaultFilter.In("GUID", chunk), limit: 100000, cancellationToken: cancellationToken));

        var exclude = new HashSet<string>(excludeItemGuids, StringComparer.OrdinalIgnoreCase);

        var candidateItemGuids = revisions
            .Where(revision => !string.IsNullOrEmpty(revision.ItemGUID))
            .Select(revision => revision.ItemGUID!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(guid => !exclude.Contains(guid))
            .ToList();

        if (candidateItemGuids.Count == 0)
        {
            return [];
        }

        var parents = await _components.ReadByIdsAsync(candidateItemGuids, cancellationToken);

        var currentByRevision = parents
            .Where(record => !string.IsNullOrEmpty(record.RevisionGuid))
            .ToDictionary(record => record.RevisionGuid!, record => record, StringComparer.OrdinalIgnoreCase);

        return SelectLiveUsage(usage.Relations.Select(relation => relation.ParentRevisionGuid), currentByRevision);
    }

    /// <summary>
    /// The same for many items at once — a targeted batch
    /// growing with the number of <paramref name="itemGuids"/>, not with the vault size: the revisions
    /// of the checked items (with deleted ones — the items themselves are already in the trash) → links to these
    /// revisions → revisions of the parents → whether the parent's revision is active
    /// (<see cref="ComponentService.ReadByIdsAsync"/>, as in <see cref="FindExternalUsageAsync"/>,
    /// but for the whole batch of parents at once). An item with no references to it does not take part in the output.
    /// </summary>
    /// <param name="budgetExceeded">
    /// Checked before each server request (between "batches"): if it returns
    /// <see langword="true"/> — the walk stops at this place, <c>Completed=false</c> in
    /// the response, part of the data already collected is discarded (the final check needs all four
    /// steps at once for each item — half cannot be finished, only started again).
    /// </param>
    public async Task<UsageBatchResult> FindExternalUsageBatchAsync(
        IReadOnlyCollection<string> itemGuids,
        Func<bool> budgetExceeded,
        CancellationToken cancellationToken)
    {
        var empty = new Dictionary<string, IReadOnlyList<LiveUsage>>(StringComparer.OrdinalIgnoreCase);

        if (itemGuids.Count == 0)
        {
            return new UsageBatchResult(empty, Completed: true);
        }

        // 1. Revisions of the checked items (including deleted ones — the items themselves are already in the trash;
        // deletion is a property of the item, not of the revision, so no extra options are needed).
        (List<ALU_ItemRevision> targetRevisions, bool step1) = await ReadInChunksWithBudgetAsync(
            itemGuids,
            chunk => _gateway.GetItemRevisionsAsync(
                VaultFilter.In("ItemGUID", chunk), limit: 100000, cancellationToken: cancellationToken),
            budgetExceeded);

        if (!step1)
        {
            return new UsageBatchResult(empty, Completed: false);
        }

        var revisionToTargetItem = targetRevisions
            .Where(revision => !string.IsNullOrEmpty(revision.ItemGUID) && !string.IsNullOrEmpty(revision.GUID))
            .ToDictionary(revision => revision.GUID, revision => revision.ItemGUID!, StringComparer.OrdinalIgnoreCase);

        if (revisionToTargetItem.Count == 0)
        {
            return new UsageBatchResult(empty, Completed: true);
        }

        // 2. Links whose child is one of these revisions.
        (List<ALU_ItemRevisionLink> links, bool step2) = await ReadInChunksWithBudgetAsync(
            revisionToTargetItem.Keys,
            chunk => _gateway.GetItemRevisionLinksAsync(
                VaultFilter.In("ChildItemRevisionGUID", chunk), limit: 100000, cancellationToken: cancellationToken),
            budgetExceeded);

        if (!step2)
        {
            return new UsageBatchResult(empty, Completed: false);
        }

        if (links.Count == 0)
        {
            return new UsageBatchResult(empty, Completed: true);
        }

        // 3. Revisions of the parents of these links — to learn which item they belong to.
        var parentRevisionGuids = links
            .Select(link => link.ParentItemRevisionGUID)
            .Where(guid => !string.IsNullOrEmpty(guid))
            .Select(guid => guid!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        (List<ALU_ItemRevision> parentRevisions, bool step3) = await ReadInChunksWithBudgetAsync(
            parentRevisionGuids,
            chunk => _gateway.GetItemRevisionsAsync(
                VaultFilter.In("GUID", chunk), limit: 100000, cancellationToken: cancellationToken),
            budgetExceeded);

        if (!step3)
        {
            return new UsageBatchResult(empty, Completed: false);
        }

        var parentItemGuids = parentRevisions
            .Where(revision => !string.IsNullOrEmpty(revision.ItemGUID))
            .Select(revision => revision.ItemGUID!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (parentItemGuids.Count == 0)
        {
            return new UsageBatchResult(empty, Completed: true);
        }

        if (budgetExceeded())
        {
            return new UsageBatchResult(empty, Completed: false);
        }

        // 4. Parent items — whether they have an active revision (as in FindExternalUsageAsync,
        // but by one call for the whole batch of parents; ReadByIdsAsync already splits into portions itself).
        IReadOnlyList<ComponentRecord> parents = await _components.ReadByIdsAsync(parentItemGuids, cancellationToken);

        var currentByRevision = parents
            .Where(record => !string.IsNullOrEmpty(record.RevisionGuid))
            .ToDictionary(record => record.RevisionGuid!, record => record, StringComparer.OrdinalIgnoreCase);

        var byTarget = new Dictionary<string, List<LiveUsage>>(StringComparer.OrdinalIgnoreCase);

        foreach (ALU_ItemRevisionLink link in links)
        {
            if (link.ChildItemRevisionGUID is not { } child
                || !revisionToTargetItem.TryGetValue(child, out string? targetItemGuid)
                || link.ParentItemRevisionGUID is not { } parentRevision
                || !currentByRevision.TryGetValue(parentRevision, out ComponentRecord? record))
            {
                continue;
            }

            if (!byTarget.TryGetValue(targetItemGuid, out List<LiveUsage>? list))
            {
                byTarget[targetItemGuid] = list = [];
            }

            list.Add(new LiveUsage(record.Hrid, record.FolderPath));
        }

        var result = new Dictionary<string, IReadOnlyList<LiveUsage>>(StringComparer.OrdinalIgnoreCase);

        foreach ((string itemGuid, List<LiveUsage> usages) in byTarget)
        {
            result[itemGuid] = usages
                .DistinctBy(usage => usage.Hrid, StringComparer.OrdinalIgnoreCase)
                .OrderBy(usage => usage.Hrid, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return new UsageBatchResult(result, Completed: true);
    }

    /// <summary>
    /// Like <see cref="VaultGateway.ReadInChunksAsync{T}"/>, but checks
    /// <paramref name="budgetExceeded"/> before each portion (the IN-list limit,
    /// <see cref="VaultFilter.MaxInValues"/>) — the time limit that was earlier checked only
    /// before the whole call, and so did not manage to stop a long walk inside it.
    /// </summary>
    private static async Task<(List<T> Items, bool Completed)> ReadInChunksWithBudgetAsync<T>(
        IEnumerable<string> values,
        Func<IReadOnlyList<string>, Task<List<T>>> read,
        Func<bool> budgetExceeded)
    {
        var collected = new List<T>();

        foreach (IReadOnlyList<string> chunk in VaultFilter.Chunk(values))
        {
            if (budgetExceeded())
            {
                return (collected, false);
            }

            collected.AddRange(await read(chunk));
        }

        return (collected, true);
    }

    /// <summary>
    /// Pure selection logic: of the link revisions, those that match the active
    /// revision of the already read component remain (the rest are references from earlier revisions).
    /// </summary>
    internal static IReadOnlyList<LiveUsage> SelectLiveUsage(
        IEnumerable<string?> parentRevisionGuids,
        IReadOnlyDictionary<string, ComponentRecord> currentByRevision)
    {
        var found = new List<LiveUsage>();

        foreach (string? revisionGuid in parentRevisionGuids)
        {
            if (revisionGuid is not null && currentByRevision.TryGetValue(revisionGuid, out ComponentRecord? record))
            {
                found.Add(new LiveUsage(record.Hrid, record.FolderPath));
            }
        }

        return found
            .DistinctBy(usage => usage.Hrid, StringComparer.OrdinalIgnoreCase)
            .OrderBy(usage => usage.Hrid, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
