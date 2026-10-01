using System.ServiceModel;
using AltiumWorkspaceMCP.Soap.Vault;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>Result of the "where used" request.</summary>
public sealed record WhereUsedResult(
    List<WhereUsedRelation> Relations,
    List<ALU_Item> ParentItems,
    int TotalCount);

/// <summary>Paged "where used" read with a completeness check.</summary>
public static class WhereUsedPaging
{
    public static async Task<WhereUsedResult> ReadAllAsync(
        Func<int, int, Task<WhereUsedResult>> readPage, int pageSize)
    {
        WhereUsedResult first = await readPage(0, pageSize);
        var relations = new List<WhereUsedRelation>(first.Relations);
        var parents = new List<ALU_Item>(first.ParentItems);

        while (relations.Count < first.TotalCount)
        {
            WhereUsedResult next = await readPage(relations.Count, pageSize);

            if (next.Relations.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Could not fully check usage: the server reported {first.TotalCount} references, "
                    + $"returned {relations.Count}. The object cannot be considered free — try again later.");
            }

            relations.AddRange(next.Relations);
            parents.AddRange(next.ParentItems);
        }

        return new WhereUsedResult(relations, parents, first.TotalCount);
    }
}

/// <summary>
/// Typed access to the Vault service: session substitution, paged reading
/// and converting server refusals into exceptions.
/// </summary>
public sealed class VaultGateway
{
    /// <summary>
    /// Protection against an unbounded selection: the server returns records in pages, and a request
    /// without an upper bound can pull tens of thousands of objects.
    /// </summary>
    public const int DefaultPageLimit = 5000;

    private readonly VaultEndpoints _endpoints;
    private readonly VaultSession _session;

    /// <summary>The vault identifier does not change, so it is read once.</summary>
    private string? _vaultGuid;

    public VaultGateway(VaultEndpoints endpoints, VaultSession session)
    {
        _endpoints = endpoints;
        _session = session;
    }

    public VaultSession Session => _session;

    // ── Reading ───────────────────────────────────────────────────────────────

    public Task<List<ALU_Folder>> GetFoldersAsync(
        string filter = "",
        _StringList? options = null,
        int limit = DefaultPageLimit,
        CancellationToken cancellationToken = default) =>
        ReadPagedAsync<ALU_Folder>(
            "GetALU_Folders",
            filter,
            async (client, session, cursor, ct) =>
            {
                var response = await client.GetALU_FoldersAsync(new GetALU_FoldersRequest
                {
                    APIVersion = VaultEndpoints.ApiVersion,
                    SessionHandle = session,
                    Filter = filter,
                    InputCursor = cursor,
                    Options = options ?? VaultRequestOptions.None(),
                });
                return (response.Records?.Cast<ALU_Folder>().ToList() ?? [], response.MethodResult);
            },
            limit,
            cancellationToken);

    public Task<List<ALU_Item>> GetItemsAsync(
        string filter = "",
        _StringList? options = null,
        int limit = DefaultPageLimit,
        CancellationToken cancellationToken = default) =>
        ReadPagedAsync<ALU_Item>(
            "GetALU_Items",
            filter,
            async (client, session, cursor, ct) =>
            {
                var response = await client.GetALU_ItemsAsync(new GetALU_ItemsRequest
                {
                    APIVersion = VaultEndpoints.ApiVersion,
                    SessionHandle = session,
                    Filter = filter,
                    InputCursor = cursor,
                    Options = options ?? VaultRequestOptions.None(),
                });
                return (response.Records?.Cast<ALU_Item>().ToList() ?? [], response.MethodResult);
            },
            limit,
            cancellationToken);

    public Task<List<ALU_ItemRevision>> GetItemRevisionsAsync(
        string filter = "",
        _StringList? options = null,
        int limit = DefaultPageLimit,
        CancellationToken cancellationToken = default) =>
        ReadPagedAsync<ALU_ItemRevision>(
            "GetALU_ItemRevisions",
            filter,
            async (client, session, cursor, ct) =>
            {
                var response = await client.GetALU_ItemRevisionsAsync(new GetALU_ItemRevisionsRequest
                {
                    APIVersion = VaultEndpoints.ApiVersion,
                    SessionHandle = session,
                    Filter = filter,
                    InputCursor = cursor,
                    Options = options ?? VaultRequestOptions.None(),
                });
                return (response.Records?.Cast<ALU_ItemRevision>().ToList() ?? [], response.MethodResult);
            },
            limit,
            cancellationToken);

    public Task<List<ALU_Component>> GetComponentsAsync(
        string filter = "",
        _StringList? options = null,
        int limit = DefaultPageLimit,
        CancellationToken cancellationToken = default) =>
        ReadPagedAsync<ALU_Component>(
            "GetALU_Components",
            filter,
            async (client, session, cursor, ct) =>
            {
                var response = await client.GetALU_ComponentsAsync(new GetALU_ComponentsRequest
                {
                    APIVersion = VaultEndpoints.ApiVersion,
                    SessionHandle = session,
                    Filter = filter,
                    InputCursor = cursor,
                    Options = options ?? VaultRequestOptions.None(),
                });
                return (response.Records?.Cast<ALU_Component>().ToList() ?? [], response.MethodResult);
            },
            limit,
            cancellationToken);

    /// <summary>
    /// Revision parameters by a separate request. Needed when the table is built by levels:
    /// the server returns parameters as a flat list noticeably faster than nested in items.
    /// </summary>
    public Task<List<ALU_ItemRevisionParameter>> GetItemRevisionParametersAsync(
        string filter = "",
        int limit = DefaultPageLimit,
        CancellationToken cancellationToken = default) =>
        ReadPagedAsync<ALU_ItemRevisionParameter>(
            "GetALU_ItemRevisionParameters",
            filter,
            async (client, session, cursor, ct) =>
            {
                var response = await client.GetALU_ItemRevisionParametersAsync(
                    new GetALU_ItemRevisionParametersRequest
                    {
                        APIVersion = VaultEndpoints.ApiVersion,
                        SessionHandle = session,
                        Filter = filter,
                        InputCursor = cursor,
                        Options = VaultRequestOptions.None(),
                    });
                return (response.Records?.Cast<ALU_ItemRevisionParameter>().ToList() ?? [], response.MethodResult);
            },
            limit,
            cancellationToken);

    /// <summary>
    /// Parameter types. For all types except text, Altium stores next to the
    /// displayed value its number in the ParameterRealValue field and works with exactly that.
    /// </summary>
    public Task<List<ALU_ParameterType>> GetParameterTypesAsync(CancellationToken cancellationToken = default) =>
        ReadPagedAsync<ALU_ParameterType>(
            "GetALU_ParameterTypes",
            null,
            async (client, session, cursor, ct) =>
            {
                var response = await client.GetALU_ParameterTypesAsync(new GetALU_ParameterTypesRequest
                {
                    APIVersion = VaultEndpoints.ApiVersion,
                    SessionHandle = session,
                    Filter = string.Empty,
                    InputCursor = cursor,
                    Options = VaultRequestOptions.None(),
                });
                return (response.Records?.Cast<ALU_ParameterType>().ToList() ?? [], response.MethodResult);
            },
            DefaultPageLimit,
            cancellationToken);

    public Task<List<ALU_ContentType>> GetContentTypesAsync(
        string filter = "",
        CancellationToken cancellationToken = default) =>
        ReadPagedAsync<ALU_ContentType>(
            "GetALU_ContentTypes",
            filter,
            async (client, session, cursor, ct) =>
            {
                var response = await client.GetALU_ContentTypesAsync(new GetALU_ContentTypesRequest
                {
                    APIVersion = VaultEndpoints.ApiVersion,
                    SessionHandle = session,
                    Filter = filter,
                    InputCursor = cursor,
                    Options = VaultRequestOptions.None(),
                });
                return (response.Records?.Cast<ALU_ContentType>().ToList() ?? [], response.MethodResult);
            },
            DefaultPageLimit,
            cancellationToken);

    public Task<List<ALU_ItemRevisionLinkType>> GetItemRevisionLinkTypesAsync(
        CancellationToken cancellationToken = default) =>
        ReadPagedAsync<ALU_ItemRevisionLinkType>(
            "GetALU_ItemRevisionLinkTypes",
            null,
            async (client, session, cursor, ct) =>
            {
                var response = await client.GetALU_ItemRevisionLinkTypesAsync(
                    new GetALU_ItemRevisionLinkTypesRequest
                    {
                        APIVersion = VaultEndpoints.ApiVersion,
                        SessionHandle = session,
                        Filter = string.Empty,
                        InputCursor = cursor,
                        Options = VaultRequestOptions.None(),
                    });
                return (response.Records?.Cast<ALU_ItemRevisionLinkType>().ToList() ?? [], response.MethodResult);
            },
            DefaultPageLimit,
            cancellationToken);

    public Task<List<ALU_FolderType>> GetFolderTypesAsync(CancellationToken cancellationToken = default) =>
        ReadPagedAsync<ALU_FolderType>(
            "GetALU_FolderTypes",
            null,
            async (client, session, cursor, ct) =>
            {
                var response = await client.GetALU_FolderTypesAsync(new GetALU_FolderTypesRequest
                {
                    APIVersion = VaultEndpoints.ApiVersion,
                    SessionHandle = session,
                    Filter = string.Empty,
                    InputCursor = cursor,
                    Options = VaultRequestOptions.None(),
                });
                return (response.Records?.Cast<ALU_FolderType>().ToList() ?? [], response.MethodResult);
            },
            DefaultPageLimit,
            cancellationToken);

    public Task<List<ALU_ItemRevisionLink>> GetItemRevisionLinksAsync(
        string filter = "",
        _StringList? options = null,
        int limit = DefaultPageLimit,
        CancellationToken cancellationToken = default) =>
        ReadPagedAsync<ALU_ItemRevisionLink>(
            "GetALU_ItemRevisionLinks",
            filter,
            async (client, session, cursor, ct) =>
            {
                var response = await client.GetALU_ItemRevisionLinksAsync(new GetALU_ItemRevisionLinksRequest
                {
                    APIVersion = VaultEndpoints.ApiVersion,
                    SessionHandle = session,
                    Filter = filter,
                    InputCursor = cursor,
                    Options = options ?? VaultRequestOptions.None(),
                });
                return (response.Records?.Cast<ALU_ItemRevisionLink>().ToList() ?? [], response.MethodResult);
            },
            limit,
            cancellationToken);

    public Task<List<ALU_LifeCycleDefinition>> GetLifeCycleDefinitionsAsync(
        CancellationToken cancellationToken = default) =>
        ReadPagedAsync<ALU_LifeCycleDefinition>(
            "GetALU_LifeCycleDefinitions",
            null,
            async (client, session, cursor, ct) =>
            {
                var response = await client.GetALU_LifeCycleDefinitionsAsync(new GetALU_LifeCycleDefinitionsRequest
                {
                    APIVersion = VaultEndpoints.ApiVersion,
                    SessionHandle = session,
                    Filter = string.Empty,
                    InputCursor = cursor,
                    Options = VaultRequestOptions.Of(
                        VaultRequestOptions.IncludeLifeCycleStates),
                });
                return (response.Records?.Cast<ALU_LifeCycleDefinition>().ToList() ?? [], response.MethodResult);
            },
            DefaultPageLimit,
            cancellationToken);

    /// <summary>Files attached to a revision (its payload).</summary>
    public Task<List<ALU_DataFile>> GetDataFilesAsync(
        string revisionGuid,
        CancellationToken cancellationToken = default) =>
        ReadPagedAsync<ALU_DataFile>(
            "GetALU_DataFiles",
            VaultFilter.Equal("ItemRevisionGUID", revisionGuid),
            async (client, session, cursor, ct) =>
            {
                var response = await client.GetALU_DataFilesAsync(new GetALU_DataFilesRequest
                {
                    APIVersion = VaultEndpoints.ApiVersion,
                    SessionHandle = session,
                    Filter = VaultFilter.Equal("ItemRevisionGUID", revisionGuid),
                    InputCursor = cursor,
                    Options = VaultRequestOptions.None(),
                });
                return (response.Records?.Cast<ALU_DataFile>().ToList() ?? [], response.MethodResult);
            },
            DefaultPageLimit,
            cancellationToken);

    public Task<List<ALU_LifeCycleState>> GetLifeCycleStatesAsync(
        CancellationToken cancellationToken = default) =>
        ReadPagedAsync<ALU_LifeCycleState>(
            "GetALU_LifeCycleStates",
            null,
            async (client, session, cursor, ct) =>
            {
                var response = await client.GetALU_LifeCycleStatesAsync(new GetALU_LifeCycleStatesRequest
                {
                    APIVersion = VaultEndpoints.ApiVersion,
                    SessionHandle = session,
                    Filter = string.Empty,
                    InputCursor = cursor,
                    Options = VaultRequestOptions.None(),
                });
                return (response.Records?.Cast<ALU_LifeCycleState>().ToList() ?? [], response.MethodResult);
            },
            DefaultPageLimit,
            cancellationToken);

    public Task<List<ALU_RevisionNamingScheme>> GetRevisionNamingSchemesAsync(
        CancellationToken cancellationToken = default) =>
        ReadPagedAsync<ALU_RevisionNamingScheme>(
            "GetALU_RevisionNamingSchemes",
            null,
            async (client, session, cursor, ct) =>
            {
                var response = await client.GetALU_RevisionNamingSchemesAsync(new GetALU_RevisionNamingSchemesRequest
                {
                    APIVersion = VaultEndpoints.ApiVersion,
                    SessionHandle = session,
                    Filter = string.Empty,
                    InputCursor = cursor,
                    Options = VaultRequestOptions.None(),
                });
                return (response.Records?.Cast<ALU_RevisionNamingScheme>().ToList() ?? [], response.MethodResult);
            },
            DefaultPageLimit,
            cancellationToken);

    public async Task<int> GetItemCountAsync(string filter = "", CancellationToken cancellationToken = default) =>
        await InvokeAsync(
            "GetALU_ItemCount",
            async (client, session, ct) =>
            {
                var response = await client.GetALU_ItemCountAsync(new GetALU_ItemCountRequest
                {
                    APIVersion = VaultEndpoints.ApiVersion,
                    SessionHandle = session,
                    Filter = filter,
                });
                VaultOperationException.ThrowIfFailed("GetALU_ItemCount", response.MethodResult);
                return response.RecordsCount;
            },
            cancellationToken);

    /// <summary>
    /// Where an item is used: "parent revision — child revision" links
    /// and the parent items themselves that these revisions belong to.
    /// </summary>
    public async Task<WhereUsedResult> GetWhereUsedAsync(
        string itemGuid,
        int limit = 500,
        CancellationToken cancellationToken = default,
        int start = 0) =>
        await InvokeAsync(
            "GetWhereUsedByItem",
            async (client, session, ct) =>
            {
                var response = await client.GetWhereUsedByItemAsync(new GetWhereUsedByItemRequest
                {
                    APIVersion = VaultEndpoints.ApiVersion,
                    SessionHandle = session,
                    Params = new WhereUsedByItemParams { ItemGuid = itemGuid, Start = start, Limit = limit },
                });
                VaultOperationException.ThrowIfFailed("GetWhereUsedByItem", response.MethodResult);
                return new WhereUsedResult(
                    response.Relations?.ToList() ?? [],
                    response.AluItems?.ToList() ?? [],
                    response.TotalCount);
            },
            cancellationToken);

    /// <summary>
    /// All references to an item: pages are read further until <c>TotalCount</c> is received.
    /// If it cannot be read to the end, throws an exception — the object cannot be considered free.
    /// </summary>
    public Task<WhereUsedResult> GetWhereUsedAllAsync(string itemGuid, CancellationToken cancellationToken) =>
        WhereUsedPaging.ReadAllAsync(
            (start, limit) => GetWhereUsedAsync(itemGuid, limit, cancellationToken, start),
            DefaultPageLimit);

    // ── Changing ──────────────────────────────────────────────────────────────

    public Task UpdateItemRevisionsAsync(
        IReadOnlyCollection<ALU_ItemRevision> revisions,
        _StringList? options = null,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            "UpdateALU_ItemRevisions",
            revisions,
            true,
            async (client, session, ct) =>
            {
                var records = new _ALU_ItemRevisionList();
                records.AddRange(revisions);
                var response = await client.UpdateALU_ItemRevisionsAsync(
                    VaultEndpoints.ApiVersion, options ?? VaultRequestOptions.None(), records, session);
                VaultOperationException.ThrowIfFailed("UpdateALU_ItemRevisions", response.MethodResult);
                return true;
            },
            cancellationToken);

    public Task AddItemRevisionsAsync(
        IReadOnlyCollection<ALU_ItemRevision> revisions,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            "AddALU_ItemRevisions",
            revisions,
            true,
            async (client, session, ct) =>
            {
                var records = new _ALU_ItemRevisionList();
                records.AddRange(revisions);
                var response = await client.AddALU_ItemRevisionsAsync(
                    VaultEndpoints.ApiVersion, VaultRequestOptions.None(), records, session);
                VaultOperationException.ThrowIfFailed("AddALU_ItemRevisions", response.MethodResult);
                return true;
            },
            cancellationToken);

    public Task AddItemRevisionLinksAsync(
        IReadOnlyCollection<ALU_ItemRevisionLink> links,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            "AddALU_ItemRevisionLinks",
            links,
            true,
            async (client, session, ct) =>
            {
                var records = new _ALU_ItemRevisionLinkList();
                records.AddRange(links);
                var response = await client.AddALU_ItemRevisionLinksAsync(
                    VaultEndpoints.ApiVersion, VaultRequestOptions.None(), records, session);
                VaultOperationException.ThrowIfFailed("AddALU_ItemRevisionLinks", response.MethodResult);
                return true;
            },
            cancellationToken);

    public Task UpdateItemsAsync(
        IReadOnlyCollection<ALU_Item> items,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            "UpdateALU_Items",
            items,
            true,
            async (client, session, ct) =>
            {
                var records = new _ALU_ItemList();
                records.AddRange(items);
                var response = await client.UpdateALU_ItemsAsync(
                    VaultEndpoints.ApiVersion, VaultRequestOptions.None(), records, session);
                VaultOperationException.ThrowIfFailed("UpdateALU_Items", response.MethodResult);
                return true;
            },
            cancellationToken);

    public Task MoveItemsAsync(
        IReadOnlyCollection<ALU_MoveItem> moves,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            "MoveALU_Items",
            moves,
            true,
            async (client, session, ct) =>
            {
                var records = new _ALU_MoveItemList();
                records.AddRange(moves);
                var response = await client.MoveALU_ItemsAsync(
                    VaultEndpoints.ApiVersion, VaultRequestOptions.None(), records, session);
                VaultOperationException.ThrowIfFailed("MoveALU_Items", response.MethodResult);
                return true;
            },
            cancellationToken);

    public Task<List<ALU_EditResult>> AddFoldersAsync(
        IReadOnlyCollection<ALU_Folder> folders,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            "AddALU_Folders",
            folders,
            new List<ALU_EditResult>(),
            async (client, session, ct) =>
            {
                var records = new _ALU_FolderList();
                records.AddRange(folders);
                var response = await client.AddALU_FoldersAsync(
                    VaultEndpoints.ApiVersion, VaultRequestOptions.None(), records, session);
                VaultOperationException.ThrowIfFailed("AddALU_Folders", response.MethodResult);
                return response.MethodResult?.Results?.ToList() ?? [];
            },
            cancellationToken);

    public Task UpdateFoldersAsync(
        IReadOnlyCollection<ALU_Folder> folders,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            "UpdateALU_Folders",
            folders,
            true,
            async (client, session, ct) =>
            {
                var records = new _ALU_FolderList();
                records.AddRange(folders);
                var response = await client.UpdateALU_FoldersAsync(
                    VaultEndpoints.ApiVersion, VaultRequestOptions.None(), records, session);
                VaultOperationException.ThrowIfFailed("UpdateALU_Folders", response.MethodResult);
                return true;
            },
            cancellationToken);

    public Task MoveFoldersAsync(
        IReadOnlyCollection<ALU_MoveFolder> moves,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            "MoveALU_Folders",
            moves,
            true,
            async (client, session, ct) =>
            {
                var records = new _ALU_MoveFolderList();
                records.AddRange(moves);
                var response = await client.MoveALU_FoldersAsync(
                    VaultEndpoints.ApiVersion, VaultRequestOptions.None(), records, session);
                VaultOperationException.ThrowIfFailed("MoveALU_Folders", response.MethodResult);
                return true;
            },
            cancellationToken);

    /// <summary>
    /// Download addresses of the revision payload — symbol and footprint
    /// library files.
    /// </summary>
    public Task<List<ALU_URLResult>> GetRevisionDownloadUrlsAsync(
        IReadOnlyCollection<string> revisionGuids,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(
            "GetALU_ItemRevisionDownloadURLs",
            async (client, session, ct) =>
            {
                var guids = new _StringList();
                guids.AddRange(revisionGuids);

                var response = await client.GetALU_ItemRevisionDownloadURLsAsync(
                    VaultEndpoints.ApiVersion, guids, VaultRequestOptions.None(), session);

                VaultOperationException.ThrowIfFailed(
                    "GetALU_ItemRevisionDownloadURLs", response.MethodResult);

                return response.MethodResult?.Results?.ToList() ?? [];
            },
            cancellationToken);

    /// <summary>
    /// Links between items. Unlike revision links, they are not versioned:
    /// this is how a datasheet is attached to a part.
    /// </summary>
    public Task<List<ALU_ItemLink>> GetItemLinksAsync(
        string filter = "",
        int limit = DefaultPageLimit,
        CancellationToken cancellationToken = default) =>
        ReadPagedAsync<ALU_ItemLink>(
            "GetALU_ItemLinks",
            filter,
            async (client, session, cursor, ct) =>
            {
                var response = await client.GetALU_ItemLinksAsync(new GetALU_ItemLinksRequest
                {
                    APIVersion = VaultEndpoints.ApiVersion,
                    SessionHandle = session,
                    Filter = filter,
                    InputCursor = cursor,
                    Options = VaultRequestOptions.Of(VaultRequestOptions.IncludeItemLinkParameters),
                });
                return (response.Records?.Cast<ALU_ItemLink>().ToList() ?? [], response.MethodResult);
            },
            limit,
            cancellationToken);

    /// <summary>
    /// Tag families. Altium stores component types exactly this way — as tags
    /// in the system family VaultTags.ComponentTypeFamilyGuid.
    /// </summary>
    public Task<List<ALU_TagFamily>> GetTagFamiliesAsync(
        string filter = "",
        CancellationToken cancellationToken = default) =>
        ReadPagedAsync<ALU_TagFamily>(
            "GetALU_TagFamilies",
            filter,
            async (client, session, cursor, ct) =>
            {
                var response = await client.GetALU_TagFamiliesAsync(new GetALU_TagFamiliesRequest
                {
                    APIVersion = VaultEndpoints.ApiVersion,
                    SessionHandle = session,
                    Filter = filter,
                    InputCursor = cursor,
                    Options = VaultRequestOptions.None(),
                });
                return (response.Records?.Cast<ALU_TagFamily>().ToList() ?? [], response.MethodResult);
            },
            DefaultPageLimit,
            cancellationToken);

    public Task<List<ALU_Tag>> GetTagsAsync(
        string filter = "",
        CancellationToken cancellationToken = default) =>
        ReadPagedAsync<ALU_Tag>(
            "GetALU_Tags",
            filter,
            async (client, session, cursor, ct) =>
            {
                var response = await client.GetALU_TagsAsync(new GetALU_TagsRequest
                {
                    APIVersion = VaultEndpoints.ApiVersion,
                    SessionHandle = session,
                    Filter = filter,
                    InputCursor = cursor,
                    Options = VaultRequestOptions.None(),
                });
                return (response.Records?.Cast<ALU_Tag>().ToList() ?? [], response.MethodResult);
            },
            DefaultPageLimit,
            cancellationToken);

    public Task AddTagsAsync(
        IReadOnlyCollection<ALU_Tag> tags,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            "AddALU_Tags",
            tags,
            true,
            async (client, session, ct) =>
            {
                var records = new _ALU_TagList();
                records.AddRange(tags);
                var response = await client.AddALU_TagsAsync(
                    VaultEndpoints.ApiVersion, VaultRequestOptions.None(), records, session);
                VaultOperationException.ThrowIfFailed("AddALU_Tags", response.MethodResult);
                return true;
            },
            cancellationToken);

    public Task UpdateTagsAsync(
        IReadOnlyCollection<ALU_Tag> tags,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            "UpdateALU_Tags",
            tags,
            true,
            async (client, session, ct) =>
            {
                var records = new _ALU_TagList();
                records.AddRange(tags);
                var response = await client.UpdateALU_TagsAsync(
                    VaultEndpoints.ApiVersion, VaultRequestOptions.None(), records, session);
                VaultOperationException.ThrowIfFailed("UpdateALU_Tags", response.MethodResult);
                return true;
            },
            cancellationToken);

    public Task DeleteTagsAsync(
        IReadOnlyCollection<string> tagGuids,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            "DeleteALU_Tags",
            tagGuids,
            true,
            async (client, session, ct) =>
            {
                var guids = new _StringList();
                guids.AddRange(tagGuids);
                var response = await client.DeleteALU_TagsAsync(
                    VaultEndpoints.ApiVersion, guids, VaultRequestOptions.None(), session);
                VaultOperationException.ThrowIfFailed("DeleteALU_Tags", response.MethodResult);
                return true;
            },
            cancellationToken);

    public Task<List<ALU_ItemTag>> GetItemTagsAsync(
        string filter = "",
        int limit = DefaultPageLimit,
        CancellationToken cancellationToken = default) =>
        ReadPagedAsync<ALU_ItemTag>(
            "GetALU_ItemTags",
            filter,
            async (client, session, cursor, ct) =>
            {
                var response = await client.GetALU_ItemTagsAsync(new GetALU_ItemTagsRequest
                {
                    APIVersion = VaultEndpoints.ApiVersion,
                    SessionHandle = session,
                    Filter = filter,
                    InputCursor = cursor,
                    Options = VaultRequestOptions.None(),
                });
                return (response.Records?.Cast<ALU_ItemTag>().ToList() ?? [], response.MethodResult);
            },
            limit,
            cancellationToken);

    /// <summary>Assigns tags to items, replacing the earlier assignments of the same family.</summary>
    public Task AssignItemTagsAsync(
        IReadOnlyCollection<ALU_ItemTag> itemTags,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            "AssignALU_ItemTags",
            itemTags,
            true,
            async (client, session, ct) =>
            {
                var records = new _ALU_ItemTagList();
                records.AddRange(itemTags);
                var response = await client.AssignALU_ItemTagsAsync(
                    VaultEndpoints.ApiVersion, VaultRequestOptions.None(), records, session);
                VaultOperationException.ThrowIfFailed("AssignALU_ItemTags", response.MethodResult);
                return true;
            },
            cancellationToken);

    /// <summary>
    /// The identifier of the workspace itself. It is needed, for example,
    /// when writing a template reference into folder parameters.
    /// </summary>
    public async Task<string> GetVaultGuidAsync(CancellationToken cancellationToken = default)
    {
        _vaultGuid ??= await InvokeAsync(
            "GetALU_VaultRecord",
            async (client, session, ct) =>
            {
                var response = await client.GetALU_VaultRecordAsync(new GetALU_VaultRecordRequest
                {
                    APIVersion = VaultEndpoints.ApiVersion,
                    SessionHandle = session,
                });

                VaultOperationException.ThrowIfFailed("GetALU_VaultRecord", response.MethodResult);
                return response.Vault?.GUID
                    ?? throw new VaultOperationException(
                        "GetALU_VaultRecord", null, "the server did not return the vault identifier");
            },
            cancellationToken);

        return _vaultGuid;
    }

    /// <summary>
    /// Moves items to the trash.
    /// </summary>
    /// <param name="force">
    /// Delete even items in use. By default items in use are skipped:
    /// deleting a component placed on a board would break the reference.
    /// </param>
    public Task<List<ALU_EditResult>> SoftDeleteItemsAsync(
        IReadOnlyCollection<string> itemGuids,
        bool force,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            "SoftDeleteALU_Items",
            itemGuids,
            new List<ALU_EditResult>(),
            async (client, session, ct) =>
            {
                var guids = new _StringList();
                guids.AddRange(itemGuids);

                var response = await client.SoftDeleteALU_ItemsAsync(
                    VaultEndpoints.ApiVersion,
                    force ? SoftDeleteMode.ForcedWithDeleteFromIndex : SoftDeleteMode.IfNotUsedWithDeleteFromIndex,
                    guids,
                    VaultRequestOptions.None(),
                    session);

                VaultOperationException.ThrowIfFailed("SoftDeleteALU_Items", response.MethodResult);
                return response.MethodResult?.Results?.ToList() ?? [];
            },
            cancellationToken);

    /// <summary>Moves folders to the trash together with the content.</summary>
    public Task<List<ALU_EditResult>> SoftDeleteFoldersAsync(
        IReadOnlyCollection<string> folderGuids,
        bool force,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            "SoftDeleteALU_Folders",
            folderGuids,
            new List<ALU_EditResult>(),
            async (client, session, ct) =>
            {
                var guids = new _StringList();
                guids.AddRange(folderGuids);

                var response = await client.SoftDeleteALU_FoldersAsync(
                    VaultEndpoints.ApiVersion,
                    force ? SoftDeleteMode.ForcedWithDeleteFromIndex : SoftDeleteMode.IfNotUsedWithDeleteFromIndex,
                    guids,
                    VaultRequestOptions.None(),
                    session);

                VaultOperationException.ThrowIfFailed("SoftDeleteALU_Folders", response.MethodResult);
                return response.MethodResult?.Results?.ToList() ?? [];
            },
            cancellationToken);

    /// <summary>Returns items from the trash.</summary>
    public Task RestoreItemsAsync(
        IReadOnlyCollection<string> itemGuids,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            "RestoreALU_Items",
            itemGuids,
            true,
            async (client, session, ct) =>
            {
                var guids = new _StringList();
                guids.AddRange(itemGuids);

                var response = await client.RestoreALU_ItemsAsync(
                    VaultEndpoints.ApiVersion, guids, VaultRequestOptions.None(), false, session);

                VaultOperationException.ThrowIfFailed("RestoreALU_Items", response.MethodResult);
                return true;
            },
            cancellationToken);

    /// <summary>
    /// Returns folders from the trash. With <paramref name="restoreContent"/> — together with the items
    /// lying directly in them. Nested folders are restored by separate calls, parents
    /// before children.
    /// </summary>
    public Task<List<ALU_RestoreResult>> RestoreFoldersAsync(
        IReadOnlyCollection<string> folderGuids,
        bool restoreContent,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            "RestoreALU_Folders",
            folderGuids,
            new List<ALU_RestoreResult>(),
            async (client, session, ct) =>
            {
                var guids = new _StringList();
                guids.AddRange(folderGuids);

                var response = await client.RestoreALU_FoldersAsync(
                    VaultEndpoints.ApiVersion, guids, VaultRequestOptions.None(), restoreContent, session);

                VaultOperationException.ThrowIfFailed("RestoreALU_Folders", response.MethodResult);
                return response.MethodResult?.Results?.ToList() ?? [];
            },
            cancellationToken);

    public Task AddItemsAsync(
        IReadOnlyCollection<ALU_Item> items,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            "AddALU_Items",
            items,
            true,
            async (client, session, ct) =>
            {
                var records = new _ALU_ItemList();
                records.AddRange(items);

                var response = await client.AddALU_ItemsAsync(
                    VaultEndpoints.ApiVersion, VaultRequestOptions.None(), records, session);

                VaultOperationException.ThrowIfFailed("AddALU_Items", response.MethodResult);
                return true;
            },
            cancellationToken);

    /// <summary>
    /// Reserves free item identifiers by the folder naming scheme.
    /// </summary>
    /// <remarks>
    /// Identifiers must not be made up: they are issued by the server, which tracks
    /// uniqueness and continues the numbering of the existing series.
    /// </remarks>
    public Task<List<string>> GenerateItemHridsAsync(
        string folderGuid,
        string contentTypeGuid,
        int count,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            "GenerateAndReserveItemHRIDs",
            new[] { $"{count} identifiers for folder {folderGuid}" },
            Enumerable.Range(1, count).Select(index => $"(new {index})").ToList(),
            async (client, session, ct) =>
            {
                var response = await client.GenerateAndReserveItemHRIDsAsync(
                    new GenerateAndReserveItemHRIDsRequest
                    {
                        APIVersion = VaultEndpoints.ApiVersion,
                        SessionHandle = session,
                        FolderGUID = folderGuid,
                        ContentTypeGUID = contentTypeGuid,
                        ItemsCount = count,
                        FirstIndex = 0,
                        ItemNamingScheme = string.Empty,
                        Options = VaultRequestOptions.None(),
                    }).ConfigureAwait(false);

                VaultOperationException.ThrowIfFailed("GenerateAndReserveItemHRIDs", response.MethodResult);
                return response.HRIDs?.ToList() ?? [];
            },
            cancellationToken);

    public Task UpdateItemRevisionLinksAsync(
        IReadOnlyCollection<ALU_ItemRevisionLink> links,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            "UpdateALU_ItemRevisionLinks",
            links,
            true,
            async (client, session, ct) =>
            {
                var records = new _ALU_ItemRevisionLinkList();
                records.AddRange(links);

                var response = await client.UpdateALU_ItemRevisionLinksAsync(
                    VaultEndpoints.ApiVersion, VaultRequestOptions.None(), records, session);

                VaultOperationException.ThrowIfFailed("UpdateALU_ItemRevisionLinks", response.MethodResult);
                return true;
            },
            cancellationToken);

    public Task DeleteItemRevisionLinksAsync(
        IReadOnlyCollection<string> linkGuids,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            "DeleteALU_ItemRevisionLinks",
            linkGuids,
            true,
            async (client, session, ct) =>
            {
                var guids = new _StringList();
                guids.AddRange(linkGuids);

                var response = await client.DeleteALU_ItemRevisionLinksAsync(
                    VaultEndpoints.ApiVersion, guids, VaultRequestOptions.None(), session);

                VaultOperationException.ThrowIfFailed("DeleteALU_ItemRevisionLinks", response.MethodResult);
                return true;
            },
            cancellationToken);

    /// <summary>
    /// Reads a selection by a set of values, splitting it into parts that fit
    /// the IN list limit, and merging the results.
    /// </summary>
    public static async Task<List<T>> ReadInChunksAsync<T>(
        IEnumerable<string> values,
        Func<IReadOnlyList<string>, Task<List<T>>> read)
    {
        var collected = new List<T>();

        foreach (IReadOnlyList<string> chunk in VaultFilter.Chunk(values))
        {
            collected.AddRange(await read(chunk));
        }

        return collected;
    }

    // ── Call infrastructure ───────────────────────────────────────────────────

    /// <summary>
    /// A call that changes the vault. In a dry run the data is only written to the report
    /// and does not go to the server, while the calling code gets a plausible response.
    /// </summary>
    private Task<T> WriteAsync<T, TRecord>(
        string operation,
        IReadOnlyCollection<TRecord> payload,
        T dryRunResult,
        Func<VaultActionServiceClient, string, CancellationToken, Task<T>> call,
        CancellationToken cancellationToken) =>
        DryRun.Intercept(operation, payload)
            ? Task.FromResult(dryRunResult)
            : InvokeAsync(operation, call, cancellationToken);

    /// <summary>
    /// Makes one service call: takes the active session, creates a WCF channel
    /// and closes it whatever the outcome.
    /// </summary>
    private Task<T> InvokeAsync<T>(
        string operation,
        Func<VaultActionServiceClient, string, CancellationToken, Task<T>> call,
        CancellationToken cancellationToken) =>
        _session.ExecuteAsync(
            async (session, ct) =>
            {
                var client = _endpoints.CreateVaultClient();
                try
                {
                    return await call(client, session, ct);
                }
                finally
                {
                    await CloseAsync(client);
                }
            },
            cancellationToken);

    /// <summary>
    /// Reads all pages of a response. The Vault service returns records in portions and passes
    /// the position of the next portion through a cursor.
    /// </summary>
    private async Task<List<T>> ReadPagedAsync<T>(
        string operation,
        string? filter,
        Func<VaultActionServiceClient, string, string, CancellationToken, Task<(List<T> Records, ALU_GetResult? Result)>> page,
        int limit,
        CancellationToken cancellationToken)
    {
        var collected = new List<T>();
        string cursor = string.Empty;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            string currentCursor = cursor;
            var (records, result) = await InvokeAsync(
                operation,
                (client, session, ct) => page(client, session, currentCursor, ct),
                cancellationToken);

            VaultOperationException.ThrowIfFailed(operation, result);
            collected.AddRange(records);

            if (collected.Count >= limit)
            {
                var limited = collected.Take(limit).ToList();
                DryRun.NoteRead(operation, filter, limited);
                return limited;
            }

            cursor = result!.MoreDataAvailable ? result.OutputCursor ?? string.Empty : string.Empty;

            if (result.MoreDataAvailable && string.IsNullOrEmpty(cursor))
            {
                throw new VaultOperationException(
                    operation, null, "the server reported a next page but returned no cursor");
            }
        }
        while (!string.IsNullOrEmpty(cursor));

        DryRun.NoteRead(operation, filter, collected);
        return collected;
    }

    private static async Task CloseAsync(ICommunicationObject client)
    {
        try
        {
            if (client.State == CommunicationState.Faulted)
            {
                client.Abort();
                return;
            }

            await Task.Factory.FromAsync(client.BeginClose, client.EndClose, null);
        }
        catch (Exception exception) when (exception is CommunicationException or TimeoutException)
        {
            client.Abort();
        }
    }
}
