using AltiumWorkspaceMCP.Soap.Vault;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>The revision a link points to and its label for people.</summary>
public sealed record LinkTarget(string RevisionGuid, string Label);

/// <summary>A component as one table row: attributes plus revision parameters.</summary>
public sealed record ComponentRecord
{
    public required string ItemGuid { get; init; }

    /// <summary>The item identifier visible to the user, for example CMP-000-00068.</summary>
    public required string Hrid { get; init; }

    public string? Description { get; init; }

    /// <summary>
    /// The component name — what Altium shows in the Comment column and puts
    /// into the schematic. Stored on the revision and edited like parameters.
    /// </summary>
    public string? Comment { get; init; }

    public required string FolderPath { get; init; }

    public required string FolderGuid { get; init; }

    public string? ContentType { get; init; }

    public string? ContentTypeGuid { get; init; }

    /// <summary>GUID of the latest revision — also the address for editing parameters.</summary>
    public string? RevisionGuid { get; init; }

    public string? RevisionId { get; init; }

    public string? LifeCycleState { get; init; }

    public string? LifeCycleStateGuid { get; init; }

    public DateTimeOffset? RevisionModifiedAt { get; init; }

    public int RevisionCount { get; init; }

    /// <summary>The whole active revision — needed for editing without re-reading.</summary>
    public ALU_ItemRevision? LatestRevision { get; init; }

    public IReadOnlyDictionary<string, string> Parameters { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Reading components in a form suitable for table processing.
/// </summary>
public sealed class ComponentService
{
    private readonly VaultGateway _gateway;
    private readonly VaultCatalog _catalog;
    private readonly ComponentSearch _search;

    public ComponentService(VaultGateway gateway, VaultCatalog catalog)
    {
        _gateway = gateway;
        _catalog = catalog;
        _search = new ComponentSearch(gateway, catalog);
    }

    /// <summary>
    /// Finds the revision a link should point to: the active revision of an object by
    /// identifier or GUID, or a revision by its GUID.
    /// </summary>
    public async Task<string> ResolveLinkTargetAsync(string target, CancellationToken cancellationToken) =>
        (await ResolveLinkTargetInfoAsync(target, cancellationToken)).RevisionGuid;

    /// <summary>
    /// The same as <see cref="ResolveLinkTargetAsync"/>, but with the target label for the preview:
    /// "PCC-000-0076 rev. 3", with a mark if the revision is not the active one.
    /// </summary>
    public async Task<LinkTarget> ResolveLinkTargetInfoAsync(string target, CancellationToken cancellationToken)
    {
        // First look for an item with the full identifier: CMP-000-0960 is an identifier
        // as a whole, not CMP-000 revision 960. Splitting by the hyphen — only if there is no such
        // item.
        var found = await ReadByIdsAsync([target], cancellationToken);

        if (found.Count == 1 && found[0].RevisionGuid is { Length: > 0 } revisionGuid)
        {
            return new LinkTarget(revisionGuid, Describe(found[0].Hrid, found[0].RevisionId, isCurrent: true));
        }

        // Perhaps the GUID of the revision itself was passed.
        var revisions = await _gateway.GetItemRevisionsAsync(
            VaultFilter.Equal("GUID", target), limit: 1, cancellationToken: cancellationToken);

        if (revisions.Count == 1)
        {
            return await DescribeRevisionAsync(revisions[0], target, cancellationToken);
        }

        // Perhaps a revision number was appended to the identifier with a hyphen: PCC-0009-3.
        if (LinkTargetId.TryParse(target, out string baseId, out string revisionNumber))
        {
            var byBase = await ReadByIdsAsync([baseId], cancellationToken);
            if (byBase.Count == 1)
            {
                return await ResolveRevisionByNumberAsync(byBase[0], revisionNumber, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"Link target '{target}' not found: expected the identifier of a symbol, "
            + "footprint or template, or the GUID of their revision.");
    }

    /// <summary>Looks for the active revision of the item of revision <paramref name="revision"/>, to mark in the label whether it is active.</summary>
    private async Task<LinkTarget> DescribeRevisionAsync(
        ALU_ItemRevision revision, string fallbackHrid, CancellationToken cancellationToken)
    {
        bool isCurrent = true;

        if (revision.ItemGUID is { Length: > 0 } itemGuid)
        {
            var owner = await ReadByIdsAsync([itemGuid], cancellationToken);
            if (owner.Count == 1)
            {
                isCurrent = string.Equals(owner[0].RevisionGuid, revision.GUID, StringComparison.OrdinalIgnoreCase);
            }
        }

        return new LinkTarget(revision.GUID, Describe(revision.ItemHRID ?? fallbackHrid, revision.RevisionId, isCurrent));
    }

    /// <summary>Finds the revision of item <paramref name="item"/> with the given number.</summary>
    private async Task<LinkTarget> ResolveRevisionByNumberAsync(
        ComponentRecord item, string revisionNumber, CancellationToken cancellationToken)
    {
        var revisions = await GetRevisionsAsync(item.ItemGuid, cancellationToken);
        ALU_ItemRevision? match = revisions.FirstOrDefault(
            revision => string.Equals(revision.RevisionId, revisionNumber, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            throw new InvalidOperationException(LinkTargetId.DescribeMissingRevision(
                item.Hrid,
                revisionNumber,
                revisions.Select(revision => revision.RevisionId ?? string.Empty)
                    .Where(id => id.Length > 0).ToList()));
        }

        bool isCurrent = string.Equals(match.GUID, item.RevisionGuid, StringComparison.OrdinalIgnoreCase);
        return new LinkTarget(match.GUID, Describe(item.Hrid, match.RevisionId, isCurrent));
    }

    private static string Describe(string hrid, string? revisionId, bool isCurrent) =>
        LinkTargetId.DescribeTarget(hrid, revisionId, isCurrent);

    /// <summary>
    /// Resolves a template's symbol or footprint: a model item identifier/GUID or the GUID of
    /// its revision — into a record of the item of the expected content type. The <c>.cmpt</c> gets the GUID of the <b>item</b>,
    /// even if a revision GUID was passed.
    /// </summary>
    /// <param name="target">Identifier (SYM-…, PCC-…), item GUID or revision GUID.</param>
    /// <param name="expectedContentType">Content type of the target, for example <c>altium-symbol</c>.</param>
    /// <param name="roleLabel">Role name for the error message, for example "Symbol".</param>
    public async Task<ComponentRecord> ResolveModelAsync(
        string target, string expectedContentType, string roleLabel, CancellationToken cancellationToken)
    {
        var found = await ReadByIdsAsync([target], cancellationToken);
        ComponentRecord? record = found.Count == 1 ? found[0] : null;

        if (record is null)
        {
            // Perhaps the GUID of the revision itself was passed — it has the ItemGUID of the item.
            var revisions = await _gateway.GetItemRevisionsAsync(
                VaultFilter.Equal("GUID", target), limit: 1, cancellationToken: cancellationToken);

            if (revisions.Count == 1 && revisions[0].ItemGUID is { Length: > 0 } itemGuid)
            {
                var byItem = await ReadByIdsAsync([itemGuid], cancellationToken);
                record = byItem.Count == 1 ? byItem[0] : null;
            }
        }

        if (record is null)
        {
            throw new InvalidOperationException(
                $"{roleLabel} '{target}' not found: expected an identifier, an item GUID or a revision GUID.");
        }

        if (!string.Equals(record.ContentType, expectedContentType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{record.Hrid} is not a {roleLabel.ToLowerInvariant()} (content type '{record.ContentType}', needed '{expectedContentType}').");
        }

        return record;
    }

    /// <summary>
    /// Refuses if not all requested components are found — with the nearest identifiers
    /// and advice on how to find the part.
    /// </summary>
    public async Task EnsureFoundAsync(
        IReadOnlyCollection<string> requested,
        IReadOnlyList<ComponentRecord> found,
        CancellationToken cancellationToken)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ComponentRecord record in found)
        {
            known.Add(record.Hrid);
            known.Add(record.ItemGuid);
        }

        var missing = requested.Where(identifier => !known.Contains(identifier)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(await DescribeMissingAsync(missing, cancellationToken));
        }
    }

    /// <summary>
    /// The "not found" refusal text: for an identifier like <c>CMP-000-99999</c> — the nearest identifiers
    /// of the same family by number, for the others — advice on how to search.
    /// </summary>
    public async Task<string> DescribeMissingAsync(IReadOnlyCollection<string> missing, CancellationToken cancellationToken)
    {
        const int Described = 5;
        var neighbours = new List<string>();

        foreach (string identifier in missing.Take(Described))
        {
            string? prefix = NameSuggester.IdentifierPrefix(identifier);
            if (prefix is null)
            {
                continue;
            }

            var siblings = await _gateway.GetItemsAsync(
                VaultFilter.Like("HRID", prefix + "%"), limit: 5000, cancellationToken: cancellationToken);

            var near = NameSuggester.NearestByNumber(identifier, siblings.Select(item => item.HRID));

            neighbours.Add(near.Count > 0
                ? $"instead of '{identifier}' there are {string.Join(", ", near)}"
                : $"nothing with the prefix '{prefix}' in the vault");
        }

        string shown = string.Join(", ", missing.Take(10)) + (missing.Count > 10 ? ", …" : string.Empty);

        return $"Not found in the vault: {shown}."
            + (neighbours.Count > 0 ? $" Nearest identifiers: {string.Join("; ", neighbours)}." : string.Empty)
            + " A part can be found through vault_table: descriptionContains='part of the description' "
            + "or parameterEquals=[\"Manufacturer Part Number=…\"].";
    }

    /// <summary>Selects components by conditions and returns them with the description of the selection.</summary>
    public async Task<(IReadOnlyList<ComponentRecord> Records, SearchPlan Plan)> SearchAsync(
        ComponentCriteria criteria,
        CancellationToken cancellationToken)
    {
        var (items, plan) = await _search.FindAsync(criteria, cancellationToken);
        return (await ProjectAsync(items, cancellationToken), plan);
    }

    /// <summary>
    /// Reads specific components by GUID or by user identifiers.
    /// The selection is split into parts: the server's IN list is limited in length.
    /// </summary>
    public async Task<IReadOnlyList<ComponentRecord>> ReadByIdsAsync(
        IReadOnlyCollection<string> identifiers,
        CancellationToken cancellationToken)
    {
        if (identifiers.Count == 0)
        {
            return [];
        }

        var distinct = identifiers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var items = await VaultGateway.ReadInChunksAsync(
            distinct,
            chunk => _gateway.GetItemsAsync(
                // The identifier may be both the item GUID and its HRID.
                VaultFilter.Or(VaultFilter.In("GUID", chunk), VaultFilter.In("HRID", chunk)),
                ComponentSearch.FullItemOptions(),
                100000,
                cancellationToken));

        return await ProjectAsync(items, cancellationToken);
    }

    /// <summary>All revisions of an item, from newest to oldest.</summary>
    public async Task<IReadOnlyList<ALU_ItemRevision>> GetRevisionsAsync(
        string itemGuid,
        CancellationToken cancellationToken)
    {
        var revisions = await _gateway.GetItemRevisionsAsync(
            VaultFilter.Equal("ItemGUID", itemGuid),
            VaultRequestOptions.Of(VaultRequestOptions.IncludeItemRevisionParameters),
            cancellationToken: cancellationToken);

        return revisions.OrderByDescending(Ordering).ToList();
    }

    private async Task<IReadOnlyList<ComponentRecord>> ProjectAsync(
        IReadOnlyList<ALU_Item> items,
        CancellationToken cancellationToken)
    {
        var folders = await _catalog.GetFoldersAsync(cancellationToken);
        var contentTypes = await _catalog.GetContentTypesAsync(cancellationToken);
        var states = await _catalog.GetLifeCycleStatesAsync(cancellationToken);

        var records = new List<ComponentRecord>(items.Count);

        foreach (ALU_Item item in items)
        {
            ALU_ItemRevision? latest = SelectLatestRevision(item);

            folders.TryGetValue(item.FolderGUID ?? string.Empty, out FolderNode? folder);
            contentTypes.TryGetValue(item.ContentTypeGUID ?? string.Empty, out ALU_ContentType? contentType);

            ALU_LifeCycleState? state = null;
            if (latest?.LifeCycleStateGUID is { Length: > 0 } stateGuid)
            {
                states.TryGetValue(stateGuid, out state);
            }

            records.Add(new ComponentRecord
            {
                ItemGuid = item.GUID,
                Hrid = item.HRID ?? string.Empty,
                // The description Altium shows is stored on the revision;
                // the item field is not always filled.
                Description = FirstNonEmpty(latest?.Description, item.Description),
                Comment = latest?.Comment,
                FolderGuid = item.FolderGUID ?? string.Empty,
                FolderPath = folder?.Path ?? string.Empty,
                ContentType = contentType?.HRID,
                ContentTypeGuid = item.ContentTypeGUID,
                RevisionGuid = latest?.GUID,
                RevisionId = latest?.RevisionId,
                LifeCycleState = state?.HRID,
                LifeCycleStateGuid = latest?.LifeCycleStateGUID,
                RevisionModifiedAt = latest is null || latest.LastModifiedAt == default
                    ? null
                    : new DateTimeOffset(latest.LastModifiedAt, TimeSpan.Zero),
                RevisionCount = item.Revisions?.Count ?? 0,
                LatestRevision = latest,
                Parameters = ExtractParameters(latest),
            });
        }

        return records;
    }

    /// <summary>
    /// The latest is the revision with the greatest creation time; on a tie
    /// the sequence number in the revision identifier decides.
    /// </summary>
    public static ALU_ItemRevision? SelectLatestRevision(ALU_Item item) =>
        item.Revisions is null || item.Revisions.Count == 0
            ? null
            : item.Revisions.MaxBy(Ordering);

    private static (DateTime Created, string Revision) Ordering(ALU_ItemRevision revision) =>
        (revision.CreatedAt, revision.RevisionId ?? string.Empty);

    private static string? FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    /// <summary>Revision parameters as "name → value"; the parameter name is stored in HRID.</summary>
    public static IReadOnlyDictionary<string, string> ExtractParameters(ALU_ItemRevision? revision)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (ALU_ItemRevisionParameter parameter in revision?.RevisionParameters ?? [])
        {
            if (!string.IsNullOrEmpty(parameter.HRID))
            {
                parameters[parameter.HRID] = parameter.ParameterValue ?? string.Empty;
            }
        }

        return parameters;
    }
}
