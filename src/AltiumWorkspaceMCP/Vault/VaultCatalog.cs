using AltiumWorkspaceMCP.Soap.Vault;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>A folder with a computed full path.</summary>
public sealed record FolderNode(
    string Guid,
    string Name,
    string Path,
    string? Description,
    string? ParentGuid,
    string? FolderTypeGuid,
    int Attributes = 0)
{
    /// <summary>The "system folder" bit in the Attributes field: Explorer hides such folders.</summary>
    public const int SystemAttribute = 1;

    /// <summary>The folder is a system one: Altium creates and hides it itself, for example Datasheets.</summary>
    public bool IsSystem => (Attributes & SystemAttribute) != 0;
}

/// <summary>
/// Vault directories needed by almost every request: the folder tree,
/// content types and lifecycles.
/// </summary>
/// <remarks>
/// The directories are read once and live in process memory: their size is small,
/// and they change incomparably less often than components. <see cref="InvalidateAsync"/>
/// resets the cache after folder structure edits.
/// </remarks>
public sealed class VaultCatalog
{
    private readonly VaultGateway _gateway;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlyDictionary<string, FolderNode>? _foldersByGuid;
    private IReadOnlyDictionary<string, ALU_ContentType>? _contentTypesByGuid;
    private IReadOnlyDictionary<string, ALU_LifeCycleState>? _lifeCycleStatesByGuid;

    public VaultCatalog(VaultGateway gateway) => _gateway = gateway;

    public async Task<IReadOnlyDictionary<string, FolderNode>> GetFoldersAsync(
        CancellationToken cancellationToken)
    {
        if (_foldersByGuid is not null)
        {
            return _foldersByGuid;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _foldersByGuid ??= await LoadFoldersAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyDictionary<string, ALU_ContentType>> GetContentTypesAsync(
        CancellationToken cancellationToken)
    {
        if (_contentTypesByGuid is not null)
        {
            return _contentTypesByGuid;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_contentTypesByGuid is null)
            {
                var types = await _gateway.GetContentTypesAsync(cancellationToken: cancellationToken);
                _contentTypesByGuid = types
                    .Where(type => !string.IsNullOrEmpty(type.GUID))
                    .GroupBy(type => type.GUID, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            }

            return _contentTypesByGuid;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyDictionary<string, ALU_LifeCycleState>> GetLifeCycleStatesAsync(
        CancellationToken cancellationToken)
    {
        if (_lifeCycleStatesByGuid is not null)
        {
            return _lifeCycleStatesByGuid;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_lifeCycleStatesByGuid is null)
            {
                var states = await _gateway.GetLifeCycleStatesAsync(cancellationToken);
                _lifeCycleStatesByGuid = states
                    .Where(state => !string.IsNullOrEmpty(state.GUID))
                    .GroupBy(state => state.GUID, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            }

            return _lifeCycleStatesByGuid;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Resets the cache after a change of the folder structure.</summary>
    public async Task InvalidateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _foldersByGuid = null;
            _contentTypesByGuid = null;
            _lifeCycleStatesByGuid = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Finds a folder strictly: GUID, full path, path ending by whole segments or a
    /// unique name (<see cref="FolderMatchMode.Strict"/>). For everything that determines where a write goes.
    /// </summary>
    public async Task<FolderNode> ResolveFolderAsync(string pathOrGuid, CancellationToken cancellationToken) =>
        (await ResolveFolderMatchAsync(pathOrGuid, FolderMatchMode.Strict, cancellationToken)).Folder;

    /// <summary>
    /// Finds a folder and says how exactly: reading allows <see cref="FolderMatchMode.Lenient"/>,
    /// and the tool response must show the substitution (<see cref="FolderMatch.Describe"/>).
    /// </summary>
    public async Task<FolderMatch> ResolveFolderMatchAsync(
        string pathOrGuid,
        FolderMatchMode mode,
        CancellationToken cancellationToken)
    {
        var folders = await GetFoldersAsync(cancellationToken);
        return FolderResolver.Resolve(folders.Values, pathOrGuid, mode);
    }

    /// <summary>A folder and all folders nested in it, including itself.</summary>
    public async Task<IReadOnlyList<FolderNode>> GetSubtreeAsync(
        string folderGuid,
        CancellationToken cancellationToken)
    {
        var folders = await GetFoldersAsync(cancellationToken);

        var byParent = folders.Values
            .Where(folder => folder.ParentGuid is not null)
            .GroupBy(folder => folder.ParentGuid!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var collected = new List<FolderNode>();
        var queue = new Queue<string>();
        queue.Enqueue(folderGuid);

        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            if (folders.TryGetValue(current, out FolderNode? node))
            {
                collected.Add(node);
            }

            if (byParent.TryGetValue(current, out List<FolderNode>? children))
            {
                foreach (FolderNode child in children)
                {
                    queue.Enqueue(child.Guid);
                }
            }
        }

        return collected;
    }

    private async Task<IReadOnlyDictionary<string, FolderNode>> LoadFoldersAsync(
        CancellationToken cancellationToken)
    {
        // Deleted folders are excluded: otherwise the trash is shown as part of the tree.
        var raw = await _gateway.GetFoldersAsync(
            options: VaultRequestOptions.Of(
                VaultRequestOptions.IncludeSystemFolders,
                VaultRequestOptions.ExcludeDeleted),
            cancellationToken: cancellationToken);

        var byGuid = raw
            .Where(folder => !string.IsNullOrEmpty(folder.GUID))
            .GroupBy(folder => folder.GUID, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
        foreach (ALU_Folder folder in byGuid.Values)
        {
            result[folder.GUID] = new FolderNode(
                Guid: folder.GUID,
                Name: folder.HRID ?? string.Empty,
                Path: BuildPath(folder, byGuid),
                Description: folder.Description,
                ParentGuid: string.IsNullOrEmpty(folder.ParentFolderGUID) ? null : folder.ParentFolderGUID,
                FolderTypeGuid: folder.FolderTypeGUID,
                Attributes: folder.Attributes);
        }

        return result;
    }

    /// <summary>
    /// Builds the path by climbing to the root along the parent references. The step counter
    /// protects against looping if the data turns out to be damaged.
    /// </summary>
    private static string BuildPath(ALU_Folder folder, IReadOnlyDictionary<string, ALU_Folder> byGuid)
    {
        var segments = new List<string>();
        ALU_Folder? current = folder;
        int guard = 0;

        while (current is not null && guard++ < 64)
        {
            segments.Add(current.HRID ?? string.Empty);

            if (string.IsNullOrEmpty(current.ParentFolderGUID)
                || !byGuid.TryGetValue(current.ParentFolderGUID, out ALU_Folder? parent))
            {
                break;
            }

            current = parent;
        }

        segments.Reverse();
        return string.Join('\\', segments);
    }
}
