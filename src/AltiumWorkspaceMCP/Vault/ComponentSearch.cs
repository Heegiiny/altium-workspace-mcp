using AltiumWorkspaceMCP.Soap.Vault;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>How the selection was done — goes into the response and explains the working time.</summary>
public sealed record SearchPlan(string Strategy, int Candidates, int Matched, long ElapsedMs)
{
    /// <summary>How the selection folder was found; empty if no folder is set.</summary>
    public FolderMatch? ResolvedFolder { get; init; }

    /// <summary>There are more components beyond the returned portion (the next portion — with a larger <c>Offset</c>).</summary>
    public bool HasMore { get; init; }
}

/// <summary>
/// Selecting components by conditions with the work moved to the server.
/// </summary>
/// <remarks>
/// Walking the whole vault with client-side filtering costs tens of seconds
/// and can bring the server down. So the narrowest condition is chosen first, the server
/// returns the list of matching items by it, and only this list
/// is read in full. Selection by a parameter value this way fits
/// into fractions of a second instead of a minute.
/// </remarks>
public sealed class ComponentSearch
{
    private readonly VaultGateway _gateway;
    private readonly VaultCatalog _catalog;

    public ComponentSearch(VaultGateway gateway, VaultCatalog catalog)
    {
        _gateway = gateway;
        _catalog = catalog;
    }

    public async Task<(IReadOnlyList<ALU_Item> Items, SearchPlan Plan)> FindAsync(
        ComponentCriteria criteria,
        CancellationToken cancellationToken)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        if (criteria.ParameterMissing.Count > 0 && string.IsNullOrWhiteSpace(criteria.Folder))
        {
            throw new ArgumentException(
                "parameterMissing requires the folder folder: without it the whole vault would have to be read "
                + "to make sure the parameter is absent.");
        }

        // The folder limits the selection both on the server and when filtering candidates.
        (IReadOnlyList<string>? folderGuids, FolderMatch? folderMatch) =
            await ResolveFolderScopeAsync(criteria, cancellationToken);

        var (items, plan) = await FindInScopeAsync(criteria, folderGuids, clock, cancellationToken);
        return (items, plan with { ResolvedFolder = folderMatch });
    }

    private async Task<(IReadOnlyList<ALU_Item> Items, SearchPlan Plan)> FindInScopeAsync(
        ComponentCriteria criteria,
        IReadOnlyList<string>? folderGuids,
        System.Diagnostics.Stopwatch clock,
        CancellationToken cancellationToken)
    {
        string? contentTypeGuid = await ResolveContentTypeAsync(criteria.ContentType, cancellationToken);

        // The narrowest condition first: a parameter selects better than a description, a description — better than a folder.
        if (criteria.Parameters.Count > 0)
        {
            return await CompleteAsync(
                "by parameter value",
                await FindByParametersAsync(criteria, cancellationToken),
                criteria, folderGuids, contentTypeGuid, clock, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(criteria.DescriptionContains))
        {
            return await CompleteAsync(
                "by description",
                await FindByDescriptionAsync(criteria.DescriptionContains, cancellationToken),
                criteria, folderGuids, contentTypeGuid, clock, cancellationToken);
        }

        // No parameters, no description — an ordinary selection by folder and identifier.
        string filter = VaultFilter.And(
            folderGuids is null ? null : VaultFilter.In("FolderGUID", folderGuids),
            BuildHridCondition(criteria.Hrid),
            contentTypeGuid is null ? null : VaultFilter.Equal("ContentTypeGUID", contentTypeGuid));

        // One more record than the portion is read: this tells whether there is anything further to page.
        var items = await _gateway.GetItemsAsync(
            filter,
            FullItemOptions(),
            criteria.ParameterMissing.Count > 0 ? 100000 : criteria.Offset + criteria.Limit + 1,
            cancellationToken);

        int candidateCount = items.Count;

        var suitable = items
            .Where(item => MatchesMissing(item, criteria.ParameterMissing))
            .Skip(criteria.Offset)
            .ToList();

        var page = suitable.Take(criteria.Limit).ToList();

        return (page, new SearchPlan("by folder and identifier", candidateCount, page.Count, clock.ElapsedMilliseconds)
        {
            HasMore = suitable.Count > criteria.Limit,
        });
    }

    /// <summary>
    /// Identifiers of items that have a parameter with the needed value.
    /// The server selects parameters itself, so the request costs fractions of a second.
    /// </summary>
    private async Task<IReadOnlyList<string>> FindByParametersAsync(
        ComponentCriteria criteria,
        CancellationToken cancellationToken)
    {
        HashSet<string>? intersection = null;

        foreach (ParameterCriterion criterion in criteria.Parameters)
        {
            string filter = VaultFilter.And(
                VaultFilter.Equal("HRID", criterion.Name),
                criterion.IsPattern
                    ? VaultFilter.Like("ParameterValue", criterion.Value)
                    : VaultFilter.Equal("ParameterValue", criterion.Value));

            var parameters = await _gateway.GetItemRevisionParametersAsync(
                filter, limit: 100000, cancellationToken: cancellationToken);

            var revisionGuids = parameters
                .Select(parameter => parameter.ItemRevisionGUID)
                .Where(guid => !string.IsNullOrEmpty(guid))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var itemGuids = await ResolveItemGuidsAsync(revisionGuids, cancellationToken);

            intersection = intersection is null
                ? [.. itemGuids]
                : [.. intersection.Intersect(itemGuids, StringComparer.OrdinalIgnoreCase)];

            if (intersection.Count == 0)
            {
                break;
            }
        }

        return intersection?.ToList() ?? [];
    }

    /// <summary>Identifiers of items in whose revision description the substring occurs.</summary>
    private async Task<IReadOnlyList<string>> FindByDescriptionAsync(
        string fragment,
        CancellationToken cancellationToken)
    {
        var revisions = await _gateway.GetItemRevisionsAsync(
            VaultFilter.Like("Description", $"%{fragment}%"),
            limit: 100000,
            cancellationToken: cancellationToken);

        return revisions
            .Select(revision => revision.ItemGUID)
            .Where(guid => !string.IsNullOrEmpty(guid))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Items the given revisions belong to.</summary>
    private async Task<IReadOnlyList<string>> ResolveItemGuidsAsync(
        IReadOnlyCollection<string> revisionGuids,
        CancellationToken cancellationToken)
    {
        if (revisionGuids.Count == 0)
        {
            return [];
        }

        var revisions = await VaultGateway.ReadInChunksAsync(
            revisionGuids,
            chunk => _gateway.GetItemRevisionsAsync(
                VaultFilter.In("GUID", chunk), limit: 100000, cancellationToken: cancellationToken));

        return revisions
            .Select(revision => revision.ItemGUID)
            .Where(guid => !string.IsNullOrEmpty(guid))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Reads the selected candidates in full and filters out those that do not pass
    /// the other conditions.
    /// </summary>
    /// <remarks>
    /// Rechecking the conditions is mandatory: a parameter with the needed value could
    /// belong to an old revision, while the selection must rely on the active one.
    /// </remarks>
    private async Task<(IReadOnlyList<ALU_Item> Items, SearchPlan Plan)> CompleteAsync(
        string strategy,
        IReadOnlyList<string> candidateGuids,
        ComponentCriteria criteria,
        IReadOnlyList<string>? folderGuids,
        string? contentTypeGuid,
        System.Diagnostics.Stopwatch clock,
        CancellationToken cancellationToken)
    {
        if (candidateGuids.Count == 0)
        {
            return ([], new SearchPlan(strategy, 0, 0, clock.ElapsedMilliseconds));
        }

        var folderScope = folderGuids is null
            ? null
            : new HashSet<string>(folderGuids, StringComparer.OrdinalIgnoreCase);

        string? hridCondition = BuildHridCondition(criteria.Hrid);

        var items = await VaultGateway.ReadInChunksAsync(
            candidateGuids,
            chunk => _gateway.GetItemsAsync(
                VaultFilter.And(
                    VaultFilter.In("GUID", chunk),
                    hridCondition,
                    contentTypeGuid is null ? null : VaultFilter.Equal("ContentTypeGUID", contentTypeGuid)),
                FullItemOptions(),
                100000,
                cancellationToken));

        var suitable = items
            .Where(item => folderScope is null || folderScope.Contains(item.FolderGUID ?? string.Empty))
            .Where(item => MatchesParameters(item, criteria.Parameters))
            .Where(item => MatchesMissing(item, criteria.ParameterMissing))
            .Where(item => MatchesDescription(item, criteria.DescriptionContains))
            .Skip(criteria.Offset)
            .ToList();

        var matched = suitable.Take(criteria.Limit).ToList();

        return (matched, new SearchPlan(strategy, candidateGuids.Count, matched.Count, clock.ElapsedMilliseconds)
        {
            HasMore = suitable.Count > criteria.Limit,
        });
    }

    /// <summary>Checks the conditions on the parameters of the active revision.</summary>
    private static bool MatchesParameters(ALU_Item item, IReadOnlyList<ParameterCriterion> criteria)
    {
        if (criteria.Count == 0)
        {
            return true;
        }

        var parameters = ComponentService.ExtractParameters(ComponentService.SelectLatestRevision(item));

        return criteria.All(criterion =>
            parameters.TryGetValue(criterion.Name, out string? value) && Matches(value, criterion.Value));
    }

    /// <summary>Checks that none of the listed parameters occurs on the part.</summary>
    private static bool MatchesMissing(ALU_Item item, IReadOnlyList<string> missing)
    {
        if (missing.Count == 0)
        {
            return true;
        }

        var parameters = ComponentService.ExtractParameters(ComponentService.SelectLatestRevision(item));
        return missing.All(name => !parameters.ContainsKey(name));
    }

    private static bool MatchesDescription(ALU_Item item, string? fragment)
    {
        if (string.IsNullOrWhiteSpace(fragment))
        {
            return true;
        }

        string? description = ComponentService.SelectLatestRevision(item)?.Description ?? item.Description;
        return description?.Contains(fragment, StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>Comparing a value with a pattern; % means any sequence.</summary>
    private static bool Matches(string value, string pattern)
    {
        if (!pattern.Contains('%', StringComparison.Ordinal))
        {
            return string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase);
        }

        string regex = "^" + string.Join(
            ".*",
            pattern.Split('%').Select(System.Text.RegularExpressions.Regex.Escape)) + "$";

        return System.Text.RegularExpressions.Regex.IsMatch(
            value, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private async Task<(IReadOnlyList<string>? Guids, FolderMatch? Match)> ResolveFolderScopeAsync(
        ComponentCriteria criteria,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(criteria.Folder))
        {
            return (null, null);
        }

        FolderMatch match = await _catalog.ResolveFolderMatchAsync(
            criteria.Folder, criteria.FolderMatch, cancellationToken);
        FolderNode folder = match.Folder;

        if (!criteria.Recursive)
        {
            return ([folder.Guid], match);
        }

        var subtree = await _catalog.GetSubtreeAsync(folder.Guid, cancellationToken);
        return (subtree.Select(node => node.Guid).ToList(), match);
    }

    private async Task<string?> ResolveContentTypeAsync(string? contentType, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var types = await _catalog.GetContentTypesAsync(cancellationToken);

        if (types.TryGetValue(contentType, out ALU_ContentType? byGuid))
        {
            return byGuid.GUID;
        }

        ALU_ContentType? byName = types.Values.FirstOrDefault(type =>
            string.Equals(type.HRID, contentType, StringComparison.OrdinalIgnoreCase));

        return byName?.GUID
            ?? throw new InvalidOperationException(
                $"Content type '{contentType}' not found. Allowed: "
                + string.Join(", ", types.Values.Select(type => type.HRID).Where(name => !string.IsNullOrEmpty(name)).OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
                + ". Details — vault_content_types.");
    }

    private static string? BuildHridCondition(string? hrid) =>
        string.IsNullOrWhiteSpace(hrid)
            ? null
            : hrid.Contains('%', StringComparison.Ordinal)
                ? VaultFilter.Like("HRID", hrid)
                : VaultFilter.Equal("HRID", hrid);

    /// <summary>
    /// Set of options for a full read of an item.
    /// </summary>
    /// <remarks>
    /// Parameters come nested: by measurements this is three times faster than a separate
    /// request for parameters by levels. Deleted ones are excluded explicitly — without this the server
    /// returns the trash content along with the active one, and just deleted
    /// components would keep appearing in selections.
    /// </remarks>
    internal static _StringList FullItemOptions() =>
        VaultRequestOptions.Of(
            VaultRequestOptions.IncludeItemRevisions,
            VaultRequestOptions.IncludeItemRevisionParameters,
            VaultRequestOptions.ExcludeDeleted,
            VaultRequestOptions.ExcludeUsers,
            VaultRequestOptions.ExcludeACLEntries);
}
