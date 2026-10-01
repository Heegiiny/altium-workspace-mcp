using AltiumWorkspaceMCP.Soap.Vault;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>What happened to the datasheets when parts were moved.</summary>
public sealed record DatasheetMove(
    IReadOnlyList<string> Moved,
    IReadOnlyList<string> Shared,
    string? TargetFolder);

/// <summary>
/// Datasheets attached to parts.
/// </summary>
/// <remarks>
/// A datasheet is an independent item of the type <c>altium-datasheet</c>, linked to
/// a part by an item-to-item link (not between revisions), so it does not follow
/// the part automatically. Altium keeps datasheets in the <c>Datasheets</c> system folder
/// next to the parts, and when a part is moved it makes sense to move the datasheet with it —
/// but only if other parts do not use it: one datasheet is often attached
/// to several values at once, and such a shared datasheet stays in place.
/// </remarks>
public sealed class DatasheetService
{
    /// <summary>Name of the system folder in which Altium keeps datasheets.</summary>
    public const string FolderName = "Datasheets";

    private const string ContentTypeName = "altium-datasheet";

    private readonly VaultGateway _gateway;
    private readonly VaultCatalog _catalog;
    private readonly FolderService _folders;

    public DatasheetService(VaultGateway gateway, VaultCatalog catalog, FolderService folders)
    {
        _gateway = gateway;
        _catalog = catalog;
        _folders = folders;
    }

    /// <summary>
    /// Moves the datasheets of the moved parts to the Datasheets folder next to them.
    /// </summary>
    /// <param name="movedItemGuids">The parts that were just moved.</param>
    /// <param name="targetFolder">The folder they were moved to.</param>
    public async Task<DatasheetMove> FollowAsync(
        IReadOnlyCollection<string> movedItemGuids,
        FolderNode targetFolder,
        CancellationToken cancellationToken)
    {
        var links = await VaultGateway.ReadInChunksAsync(
            movedItemGuids,
            chunk => _gateway.GetItemLinksAsync(
                VaultFilter.In("ParentItemGUID", chunk), limit: 100000, cancellationToken: cancellationToken));

        var candidateGuids = links
            .Select(link => link.ChildItemGUID)
            .Where(guid => !string.IsNullOrEmpty(guid))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidateGuids.Count == 0)
        {
            return new DatasheetMove([], [], null);
        }

        var datasheets = await LoadDatasheetsAsync(candidateGuids, cancellationToken);
        if (datasheets.Count == 0)
        {
            return new DatasheetMove([], [], null);
        }

        // A datasheet moves only together with everyone who uses it.
        var owners = await LoadOwnersAsync(datasheets.Select(sheet => sheet.GUID).ToList(), cancellationToken);
        var moving = new HashSet<string>(movedItemGuids, StringComparer.OrdinalIgnoreCase);

        var personal = new List<ALU_Item>();
        var shared = new List<string>();

        foreach (ALU_Item sheet in datasheets)
        {
            var users = owners.GetValueOrDefault(sheet.GUID, []);

            if (users.Any(owner => !moving.Contains(owner)))
            {
                shared.Add(sheet.HRID ?? sheet.GUID);
            }
            else
            {
                personal.Add(sheet);
            }
        }

        if (personal.Count == 0)
        {
            return new DatasheetMove([], shared, null);
        }

        FolderNode folder = await EnsureFolderAsync(targetFolder, cancellationToken);

        var relocating = personal
            .Where(sheet => !string.Equals(sheet.FolderGUID, folder.Guid, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (relocating.Count > 0)
        {
            await _gateway.MoveItemsAsync(
                relocating.Select(sheet => new ALU_MoveItem { GUID = sheet.GUID, FolderGUID = folder.Guid }).ToList(),
                cancellationToken);
        }

        return new DatasheetMove(
            relocating.Select(sheet => sheet.HRID ?? sheet.GUID).ToList(),
            shared,
            folder.Path);
    }

    /// <summary>Finds or creates the Datasheets folder inside the given one.</summary>
    private async Task<FolderNode> EnsureFolderAsync(FolderNode parent, CancellationToken cancellationToken)
    {
        var subtree = await _catalog.GetSubtreeAsync(parent.Guid, cancellationToken);

        FolderNode? existing = subtree.FirstOrDefault(node =>
            string.Equals(node.ParentGuid, parent.Guid, StringComparison.OrdinalIgnoreCase)
            && string.Equals(node.Name, FolderName, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            return existing;
        }

        // The system folder — with the same fields as Altium: type, system bit, name scheme.
        return await _folders.CreateDatasheetFolderAsync(parent, cancellationToken);
    }

    private async Task<List<ALU_Item>> LoadDatasheetsAsync(
        IReadOnlyCollection<string> itemGuids,
        CancellationToken cancellationToken)
    {
        var types = await _catalog.GetContentTypesAsync(cancellationToken);

        ALU_ContentType? datasheetType = types.Values.FirstOrDefault(type =>
            string.Equals(type.HRID, ContentTypeName, StringComparison.OrdinalIgnoreCase));

        if (datasheetType is null)
        {
            return [];
        }

        return await VaultGateway.ReadInChunksAsync(
            itemGuids,
            chunk => _gateway.GetItemsAsync(
                VaultFilter.And(
                    VaultFilter.In("GUID", chunk),
                    VaultFilter.Equal("ContentTypeGUID", datasheetType.GUID)),
                limit: 100000,
                cancellationToken: cancellationToken));
    }

    /// <summary>For each datasheet — the parts that reference it.</summary>
    private async Task<Dictionary<string, List<string>>> LoadOwnersAsync(
        IReadOnlyCollection<string> datasheetGuids,
        CancellationToken cancellationToken)
    {
        var links = await VaultGateway.ReadInChunksAsync(
            datasheetGuids,
            chunk => _gateway.GetItemLinksAsync(
                VaultFilter.In("ChildItemGUID", chunk), limit: 100000, cancellationToken: cancellationToken));

        return links
            .Where(link => !string.IsNullOrEmpty(link.ChildItemGUID) && !string.IsNullOrEmpty(link.ParentItemGUID))
            .GroupBy(link => link.ChildItemGUID, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(link => link.ParentItemGUID).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);
    }
}
