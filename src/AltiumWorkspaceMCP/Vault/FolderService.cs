using AltiumWorkspaceMCP.Soap.Vault;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>
/// Operations on the vault folder structure: creating, renaming,
/// moving and deleting.
/// </summary>
/// <remarks>
/// The folder is the first of the three coordinates of a component in Altium; the other two are set by
/// the template and the component type. The operations of this class create no revisions: they
/// change the placement and properties of the folder itself, not the component content.
/// </remarks>
public sealed class FolderService
{
    private readonly VaultGateway _gateway;
    private readonly VaultCatalog _catalog;

    public FolderService(VaultGateway gateway, VaultCatalog catalog)
    {
        _gateway = gateway;
        _catalog = catalog;
    }

    /// <summary>Name of the folder parameter in which Altium stores the naming scheme of its items.</summary>
    private const string NamingSchemeParameter = "$$!NAMING_SCHEME!$$";

    // The Datasheets system folder — exactly how Altium creates it
    // (checked against the requests Altium Designer itself sends).

    /// <summary>Name of the Datasheets system folder.</summary>
    public const string DatasheetFolderName = "Datasheets";

    /// <summary>The Datasheets folder type in Altium is special, not the parent's type.</summary>
    public const string DatasheetFolderTypeGuid = "3893BDB4-A89C-477A-B5BB-D4D52E424DF1";

    /// <summary>Naming scheme of the Datasheets folder items.</summary>
    public const string DatasheetNamingScheme = "$CONTENT_TYPE_CODE-001-{A00}";

    /// <summary>Creates a folder inside the given parent.</summary>
    /// <param name="folderType">
    /// Name or GUID of the folder type. Empty — inherited from the parent: the type determines
    /// which objects the folder accepts, and for a component library it must
    /// match the parent's.
    /// </param>
    /// <param name="namingScheme">
    /// Naming scheme of the items created in the folder, for example "CMP-016-{00000}".
    /// Empty — no scheme is set (inherited the same way the server does it
    /// for a folder without its own parameter).
    /// </param>
    public async Task<(FolderNode Folder, string? NamingScheme)> CreateAsync(
        string name,
        string parentPathOrGuid,
        string? description,
        string? folderType,
        string? namingScheme,
        int attributes,
        CancellationToken cancellationToken)
    {
        FolderNode parent = await _catalog.ResolveFolderAsync(parentPathOrGuid, cancellationToken);

        var existing = await _catalog.GetSubtreeAsync(parent.Guid, cancellationToken);
        if (existing.Any(node =>
                node.ParentGuid == parent.Guid
                && string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Folder '{parent.Path}' already has a nested folder '{name}'.");
        }

        string? folderTypeGuid = folderType is null
            ? parent.FolderTypeGuid
            : await ResolveFolderTypeAsync(folderType, cancellationToken);

        string folderGuid = NewGuid();

        var folder = new ALU_Folder
        {
            GUID = folderGuid,
            HRID = name,
            Description = description ?? string.Empty,
            ParentFolderGUID = parent.Guid,
            FolderTypeGUID = folderTypeGuid,
            Attributes = attributes,
            IsActive = true,
            FolderParameters = BuildNamingSchemeParameters(folderGuid, namingScheme),
        };

        await _gateway.AddFoldersAsync([folder], cancellationToken);

        // In a dry run the folder is not created and there is nowhere to re-read it.
        if (DryRun.IsActive)
        {
            return (
                new FolderNode(folder.GUID, name, $"{parent.Path}\\{name}", folder.Description, parent.Guid, folderTypeGuid, attributes),
                namingScheme);
        }

        await _catalog.InvalidateAsync(cancellationToken);

        FolderNode created = await _catalog.ResolveFolderAsync(folder.GUID, cancellationToken);
        return (created, await ReadNamingSchemeAsync(created.Guid, cancellationToken));
    }

    /// <summary>Parsing of the path list for <see cref="CreateManyAsync"/> without writing.</summary>
    public async Task<FolderCreationPlan> PlanCreateManyAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken) =>
        FolderCreationPlanner.Plan((await _catalog.GetFoldersAsync(cancellationToken)).Values, paths);

    /// <summary>Result of batch creation: what was written and by which method.</summary>
    public sealed record CreateManyResult(IReadOnlyList<PlannedFolder> Created, string Strategy);

    /// <summary>
    /// Creates folders from a list: parents before children, by one <c>AddALU_Folders</c> call. If
    /// the server rejects such a call (a reference to a folder from the same call), the plan is recomputed and
    /// the folders go by levels — one call per depth.
    /// </summary>
    /// <param name="plan">Path parsing; the first <paramref name="limit"/> folders are written.</param>
    public async Task<CreateManyResult> CreateManyAsync(
        FolderCreationPlan plan,
        IReadOnlyList<string> requestedPaths,
        int limit,
        string? description,
        string? folderType,
        string? namingScheme,
        CancellationToken cancellationToken)
    {
        string? typeOverride = folderType is null
            ? null
            : await ResolveFolderTypeAsync(folderType, cancellationToken);

        ALU_Folder ToRecord(PlannedFolder planned)
        {
            return new ALU_Folder
            {
                GUID = planned.Guid,
                HRID = planned.Name,
                Description = description ?? string.Empty,
                ParentFolderGUID = planned.ParentGuid,
                FolderTypeGUID = typeOverride ?? planned.InheritedTypeGuid,
                Attributes = 0,
                IsActive = true,
                FolderParameters = BuildNamingSchemeParameters(planned.Guid, namingScheme),
            };
        }

        var batch = plan.ToCreate.Take(limit).ToList();

        try
        {
            await _gateway.AddFoldersAsync(batch.Select(ToRecord).ToList(), cancellationToken);

            if (!DryRun.IsActive)
            {
                await _catalog.InvalidateAsync(cancellationToken);
            }

            return new CreateManyResult(batch, "single");
        }
        catch (VaultOperationException) when (batch.Count > 1 && !DryRun.IsActive)
        {
            // Recomputation by a fresh tree: some folders may have appeared after all.
            await _catalog.InvalidateAsync(cancellationToken);
            var replanned = (await PlanCreateManyAsync(requestedPaths, cancellationToken)).ToCreate
                .Where(folder => batch.Any(planned => string.Equals(planned.Path, folder.Path, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            foreach (var level in replanned.GroupBy(folder => folder.Depth).OrderBy(group => group.Key))
            {
                await _gateway.AddFoldersAsync(level.Select(ToRecord).ToList(), cancellationToken);
                await _catalog.InvalidateAsync(cancellationToken);
            }

            return new CreateManyResult(replanned, "perLevel");
        }
    }

    /// <summary>
    /// Creates the Datasheets system folder with all fields as Altium does: a special type, the naming
    /// scheme <c>$CONTENT_TYPE_CODE-001-{A00}</c>, the system bit in <c>Attributes</c> (Explorer
    /// hides such folders), an empty description.
    /// </summary>
    public async Task<FolderNode> CreateDatasheetFolderAsync(
        FolderNode parent,
        CancellationToken cancellationToken)
    {
        (FolderNode created, _) = await CreateAsync(
            DatasheetFolderName,
            parent.Guid,
            description: string.Empty,
            folderType: DatasheetFolderTypeGuid,
            namingScheme: DatasheetNamingScheme,
            attributes: FolderNode.SystemAttribute,
            cancellationToken);

        return created;
    }

    /// <summary>How one Datasheets folder differs from the Altium reference; empty — it does not differ.</summary>
    public sealed record SystemFolderReport(FolderNode Folder, IReadOnlyList<string> Differences);

    /// <summary>
    /// All Datasheets folders of the vault (or one given) and their differences from the Altium reference: the folder
    /// type, the system bit, the naming scheme.
    /// </summary>
    public async Task<IReadOnlyList<SystemFolderReport>> InspectSystemFoldersAsync(
        string? folder,
        CancellationToken cancellationToken)
    {
        var nodes = await _catalog.GetFoldersAsync(cancellationToken);

        string? onlyGuid = string.IsNullOrWhiteSpace(folder)
            ? null
            : (await _catalog.ResolveFolderAsync(folder, cancellationToken)).Guid;

        var targets = nodes.Values
            .Where(node => string.Equals(node.Name, DatasheetFolderName, StringComparison.OrdinalIgnoreCase))
            .Where(node => onlyGuid is null || string.Equals(node.Guid, onlyGuid, StringComparison.OrdinalIgnoreCase))
            .OrderBy(node => node.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var reports = new List<SystemFolderReport>(targets.Count);

        foreach (FolderNode node in targets)
        {
            string? scheme = await ReadNamingSchemeAsync(node.Guid, cancellationToken);

            reports.Add(new SystemFolderReport(node, DescribeDifferences(node, scheme)));
        }

        return reports;
    }

    /// <summary>
    /// Naming schemes are equal up to the counter width: Altium sends
    /// <c>{A00}</c>, while the server stores and returns <c>{A0000}</c> — this is one and the same scheme (checked
    /// on 92 Datasheets folders of the vault created by Altium itself).
    /// </summary>
    internal static bool SameNamingScheme(string? actual, string? expected) =>
        string.Equals(NormalizeScheme(actual), NormalizeScheme(expected), StringComparison.Ordinal);

    private static string? NormalizeScheme(string? scheme) =>
        scheme is null ? null : System.Text.RegularExpressions.Regex.Replace(scheme.Trim(), @"\{([A-Za-z])0+\}", "{$1}");

    /// <summary>How a Datasheets folder differs from the reference (pure logic).</summary>
    internal static IReadOnlyList<string> DescribeDifferences(FolderNode node, string? namingScheme)
    {
        var differences = new List<string>();

        if (!string.Equals(node.FolderTypeGuid, DatasheetFolderTypeGuid, StringComparison.OrdinalIgnoreCase))
        {
            differences.Add($"folder type {node.FolderTypeGuid ?? "(none)"} instead of {DatasheetFolderTypeGuid}");
        }

        if (!node.IsSystem)
        {
            differences.Add($"Attributes = {node.Attributes} instead of {FolderNode.SystemAttribute} (the folder is visible in Explorer)");
        }

        if (!SameNamingScheme(namingScheme, DatasheetNamingScheme))
        {
            differences.Add($"naming scheme '{namingScheme ?? "(none)"}' instead of '{DatasheetNamingScheme}'");
        }

        return differences;
    }

    /// <summary>
    /// Brings the differing Datasheets folders to the Altium reference with one record per folder
    /// (<c>UpdateALU_Folders</c>): type, system bit, naming scheme. The content is not touched.
    /// </summary>
    public async Task<IReadOnlyList<SystemFolderReport>> RepairSystemFoldersAsync(
        string? folder,
        CancellationToken cancellationToken)
    {
        var reports = (await InspectSystemFoldersAsync(folder, cancellationToken))
            .Where(report => report.Differences.Count > 0)
            .ToList();

        foreach (SystemFolderReport report in reports)
        {
            ALU_Folder record = await LoadAsync(report.Folder.Guid, cancellationToken);

            record.FolderTypeGUID = DatasheetFolderTypeGuid;
            record.Attributes |= FolderNode.SystemAttribute;
            record.FolderParameters = WithNamingScheme(record, DatasheetNamingScheme);

            await _gateway.UpdateFoldersAsync([record], cancellationToken);
        }

        if (reports.Count > 0)
        {
            await _catalog.InvalidateAsync(cancellationToken);
        }

        return reports;
    }

    /// <summary>Folder parameters with the given naming scheme: an existing parameter is changed, a missing one is added.</summary>
    private static _ALU_FolderParameterList WithNamingScheme(ALU_Folder folder, string scheme)
    {
        var parameters = folder.FolderParameters ?? new _ALU_FolderParameterList();

        ALU_FolderParameter? existing = parameters.FirstOrDefault(parameter =>
            string.Equals(parameter.HRID, NamingSchemeParameter, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.DefaultValue = scheme;
            return parameters;
        }

        parameters.Add(new ALU_FolderParameter
        {
            GUID = NewGuid(),
            HRID = NamingSchemeParameter,
            FolderGUID = folder.GUID,
            DefaultValue = scheme,
        });

        return parameters;
    }

    private static _ALU_FolderParameterList? BuildNamingSchemeParameters(string folderGuid, string? namingScheme)
    {
        if (string.IsNullOrWhiteSpace(namingScheme))
        {
            return null;
        }

        return new _ALU_FolderParameterList
        {
            new ALU_FolderParameter
            {
                GUID = NewGuid(),
                HRID = NamingSchemeParameter,
                FolderGUID = folderGuid,
                DefaultValue = namingScheme,
            },
        };
    }

    /// <summary>The naming scheme actually written by the server in the folder parameters.</summary>
    private async Task<string?> ReadNamingSchemeAsync(string folderGuid, CancellationToken cancellationToken)
    {
        var folders = await _gateway.GetFoldersAsync(
            VaultFilter.Equal("GUID", folderGuid),
            VaultRequestOptions.Of(VaultRequestOptions.IncludeFolderParameters, VaultRequestOptions.IncludeSystemFolders),
            limit: 1,
            cancellationToken: cancellationToken);

        return folders.Count == 1
            ? folders[0].FolderParameters
                ?.FirstOrDefault(parameter => string.Equals(parameter.HRID, NamingSchemeParameter, StringComparison.OrdinalIgnoreCase))
                ?.DefaultValue
            : null;
    }

    /// <summary>Changes the name and description of a folder without touching its content.</summary>
    public async Task<FolderNode> UpdateAsync(
        string pathOrGuid,
        string? newName,
        string? newDescription,
        CancellationToken cancellationToken)
    {
        ALU_Folder folder = await LoadAsync(pathOrGuid, cancellationToken);

        if (newName is not null)
        {
            folder.HRID = newName;
        }

        if (newDescription is not null)
        {
            folder.Description = newDescription;
        }

        await _gateway.UpdateFoldersAsync([folder], cancellationToken);

        if (DryRun.IsActive)
        {
            FolderNode current = await _catalog.ResolveFolderAsync(folder.GUID, cancellationToken);
            int cut = current.Path.LastIndexOf('\\');
            string name = folder.HRID ?? current.Name;

            return current with
            {
                Name = name,
                Path = cut < 0 ? name : current.Path[..(cut + 1)] + name,
                Description = folder.Description,
            };
        }

        await _catalog.InvalidateAsync(cancellationToken);

        return await _catalog.ResolveFolderAsync(folder.GUID, cancellationToken);
    }

    /// <summary>Moves a folder with all its content under another parent.</summary>
    public async Task<FolderNode> MoveAsync(
        string pathOrGuid,
        string newParentPathOrGuid,
        CancellationToken cancellationToken)
    {
        FolderNode folder = await _catalog.ResolveFolderAsync(pathOrGuid, cancellationToken);
        FolderNode parent = await _catalog.ResolveFolderAsync(newParentPathOrGuid, cancellationToken);

        if (string.Equals(folder.Guid, parent.Guid, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A folder cannot be moved into itself.");
        }

        // Moving a folder into its own subtree would break the tree.
        var subtree = await _catalog.GetSubtreeAsync(folder.Guid, cancellationToken);
        if (subtree.Any(node => string.Equals(node.Guid, parent.Guid, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"'{parent.Path}' is inside '{folder.Path}': such a move would break the tree.");
        }

        // The server answers such a case with an unclear "Duplicate folder HRID",
        // so a name conflict is checked in advance.
        var siblings = await _catalog.GetSubtreeAsync(parent.Guid, cancellationToken);
        if (siblings.Any(node =>
                string.Equals(node.ParentGuid, parent.Guid, StringComparison.OrdinalIgnoreCase)
                && string.Equals(node.Name, folder.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Folder '{parent.Path}' already has a nested folder '{folder.Name}'. "
                + "Rename one of them or move the content separately.");
        }

        await _gateway.MoveFoldersAsync(
            [new ALU_MoveFolder { GUID = folder.Guid, ParentFolderGUID = parent.Guid }],
            cancellationToken);

        if (DryRun.IsActive)
        {
            return folder with { ParentGuid = parent.Guid, Path = $"{parent.Path}\\{folder.Name}" };
        }

        await _catalog.InvalidateAsync(cancellationToken);
        return await _catalog.ResolveFolderAsync(folder.Guid, cancellationToken);
    }

    /// <summary>
    /// What deleting a folder will affect: the subtree of folders and the live items in them. The object count goes
    /// to the guarded-mode confirmation and the plan; earlier only the folder itself was counted,
    /// which let the deletion of a whole folder tree go through without confirmation.
    /// </summary>
    public sealed record FolderDeletionPlan(
        FolderNode Root,
        IReadOnlyList<FolderNode> Subtree,
        IReadOnlyList<ALU_Item> Items)
    {
        public int ObjectCount => Subtree.Count + Items.Count;
    }

    /// <summary>Counts the deletion volume: subtree folders and the live items in them (without writing).</summary>
    public async Task<FolderDeletionPlan> PlanDeleteAsync(string pathOrGuid, CancellationToken cancellationToken)
    {
        FolderNode folder = await _catalog.ResolveFolderAsync(pathOrGuid, cancellationToken);
        var subtree = await _catalog.GetSubtreeAsync(folder.Guid, cancellationToken);
        var subtreeGuids = subtree.Select(node => node.Guid).ToList();

        var items = subtreeGuids.Count == 0
            ? []
            : await VaultGateway.ReadInChunksAsync(
                subtreeGuids,
                chunk => _gateway.GetItemsAsync(
                    VaultFilter.In("FolderGUID", chunk),
                    // Without ExcludeDeleted the server returns what is already in the trash mixed in.
                    VaultRequestOptions.Of(VaultRequestOptions.ExcludeDeleted),
                    limit: 100000,
                    cancellationToken: cancellationToken));

        return new FolderDeletionPlan(folder, subtree, items);
    }

    /// <summary>Refusal text: which model, how many parts and templates, what to do.</summary>
    public static string DescribeBlocked(FolderNode root, IReadOnlyList<BlockedModel> blocked) =>
        $"Refused: deleting '{root.Path}' would break references — {blocked.Count} "
        + $"item{(blocked.Count == 1 ? string.Empty : "s")} of the subtree are referenced by active revisions "
        + $"of live parts or by templates outside the subtree: {DeletionGuard.DescribeDetails(blocked)}. First "
        + "move these models (vault_move_items) to where the structure is moving, then repeat the deletion.";

    /// <summary>
    /// Moves a folder to the trash. The content goes to the same place, from where it can be
    /// restored — vault_restore_items or by means of Altium.
    /// </summary>
    /// <remarks>
    /// Counting the volume and refusing on external references is the job of the calling tool
    /// (<see cref="PlanDeleteAsync"/>, <see cref="DeletionGuard"/>): here only
    /// the write itself, so that a dry run sees exactly it.
    /// </remarks>
    public async Task<int> DeleteAsync(FolderDeletionPlan plan, bool force, CancellationToken cancellationToken)
    {
        // Child folders are deleted before the parent: otherwise the server refuses because of references.
        var ordered = plan.Subtree
            .OrderByDescending(node => node.Path.Count(character => character == '\\'))
            .Select(node => node.Guid)
            .ToList();

        await _gateway.SoftDeleteFoldersAsync(ordered, force, cancellationToken);
        await _catalog.InvalidateAsync(cancellationToken);

        return ordered.Count;
    }

    private async Task<ALU_Folder> LoadAsync(string pathOrGuid, CancellationToken cancellationToken)
    {
        FolderNode node = await _catalog.ResolveFolderAsync(pathOrGuid, cancellationToken);

        // IncludeSystemFolders is required: otherwise system folders are not found.
        var folders = await _gateway.GetFoldersAsync(
            VaultFilter.Equal("GUID", node.Guid),
            VaultRequestOptions.Of(
                VaultRequestOptions.IncludeFolderParameters,
                VaultRequestOptions.IncludeSystemFolders),
            limit: 1,
            cancellationToken: cancellationToken);

        return folders.Count == 1
            ? folders[0]
            : throw new InvalidOperationException($"Folder '{pathOrGuid}' not found.");
    }

    private async Task<string> ResolveFolderTypeAsync(string folderType, CancellationToken cancellationToken)
    {
        var types = await _gateway.GetFolderTypesAsync(cancellationToken);

        ALU_FolderType? match = types.FirstOrDefault(type =>
            string.Equals(type.GUID, folderType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(type.HRID, folderType, StringComparison.OrdinalIgnoreCase));

        return match?.GUID
            ?? throw new InvalidOperationException(
                $"Folder type '{folderType}' not found. vault_folder_types lists the available types.");
    }

    private static string NewGuid() => Guid.NewGuid().ToString("D").ToUpperInvariant();
}
