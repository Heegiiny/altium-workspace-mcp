using AltiumWorkspaceMCP.Soap.Vault;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>One file a new model is made from.</summary>
/// <param name="File">Path to .PcbLib or .SchLib — in agent or server paths.</param>
/// <param name="Name">Model name (revision comment); empty — the file name without extension.</param>
public sealed record ModelCreateSpec(string File, string? Name, string? Description)
{
    /// <summary>Collects records from file and files; a name is set only for one file.</summary>
    public static List<ModelCreateSpec> Build(string? file, string[]? files, string? name, string? description)
    {
        var paths = (files ?? []).Append(file ?? string.Empty)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (paths.Count == 0)
        {
            throw new ArgumentException("For create specify file or files — the path to .PcbLib or .SchLib.", nameof(file));
        }

        if (paths.Count > 1 && !string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException(
                "name sets the name of one model and cannot be combined with several files: each name is taken from the file name.", nameof(name));
        }

        return paths.Select(path => new ModelCreateSpec(path, name, description)).ToList();
    }
}

/// <summary>A verified record of a future model.</summary>
public sealed record ModelCreateEntry(
    string ContentType,
    string ContentTypeGuid,
    string Role,
    string Name,
    string Description,
    string SourcePath,
    string FileName,
    long Size,
    IReadOnlyList<string> LibraryItems);

/// <summary>A verified creation plan: everything is already reconciled before writing.</summary>
/// <param name="Donors">Samples of service fields — by content type name.</param>
public sealed record ModelCreatePlan(
    FolderNode Folder,
    string NamingScheme,
    IReadOnlyList<ModelCreateEntry> Entries,
    IReadOnlyDictionary<string, ModelDonor> Donors,
    ComponentRecord? Component,
    IReadOnlyList<string> Notes);

/// <summary>An existing item of the same content type from which the service fields are taken.</summary>
public sealed record ModelDonor(string Hrid, ALU_Item Item, ALU_ItemRevision? Revision);

/// <summary>A created model.</summary>
public sealed record CreatedModel(
    string Hrid,
    string ItemGuid,
    string RevisionGuid,
    string Role,
    string ContentType,
    string Name,
    string FileName,
    long Size,
    string SampleHrid);

/// <summary>Creation result: what was created and, if something broke, the reason.</summary>
public sealed record ModelCreateOutcome(IReadOnlyList<CreatedModel> Created, string? Failure);

public sealed partial class ModelFileService
{
    private const string NamingSchemeParameter = "$$!NAMING_SCHEME!$$";

    private static readonly Dictionary<string, string> ContentTypesByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pcblib"] = "altium-pcb-component",
        [".schlib"] = "altium-symbol",
    };

    /// <summary>
    /// Verifies everything before writing: files, names, the folder and its naming scheme, samples of service fields,
    /// the component to link to. Writes nothing.
    /// </summary>
    public async Task<ModelCreatePlan> PlanCreateAsync(
        IReadOnlyList<ModelCreateSpec> specs,
        string folderPathOrGuid,
        string? component,
        CancellationToken cancellationToken)
    {
        if (specs.Count == 0)
        {
            throw new InvalidOperationException("No files specified: pass file or files.");
        }

        FolderNode folder = await _catalog.ResolveFolderAsync(folderPathOrGuid, cancellationToken);
        string scheme = await FindNamingSchemeAsync(folder, cancellationToken);

        var notes = new List<string>();
        var entries = new List<ModelCreateEntry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ModelCreateSpec spec in specs)
        {
            string path = _exchange.ToWindowsPath(spec.File);

            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"File '{spec.File}' not found (server path: {path}).");
            }

            string extension = Path.GetExtension(path);

            if (!ContentTypesByExtension.TryGetValue(extension, out string? contentType))
            {
                throw new InvalidOperationException(
                    $"'{Path.GetFileName(path)}': the extension {extension} is not supported. A new model is made "
                    + "from .PcbLib (footprint) or .SchLib (symbol).");
            }

            string name = string.IsNullOrWhiteSpace(spec.Name)
                ? Path.GetFileNameWithoutExtension(path)
                : spec.Name.Trim();

            if (!names.Add($"{contentType}|{name}"))
            {
                throw new InvalidOperationException(
                    $"The call has two files of the same type with the name '{name}'. Give them different names (name).");
            }

            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            bool footprint = string.Equals(contentType, "altium-pcb-component", StringComparison.Ordinal);
            var items = LibraryFileInspector.ListItems(bytes, footprint);

            if (items.Count > 1)
            {
                notes.Add($"'{Path.GetFileName(path)}' is a library of {items.Count} "
                    + $"{(footprint ? "footprints" : "symbols")} ({string.Join(", ", items.Take(8))}"
                    + $"{(items.Count > 8 ? ", …" : string.Empty)}), but in the vault one model is one file. "
                    + "One model was created from the whole file; splitting the library into separate files is the job of "
                    + "altium-designer-mcp, then create for each file.");
            }
            else if (items.Count == 0)
            {
                notes.Add($"'{Path.GetFileName(path)}': the library contents could not be read — check that the file opens in Altium.");
            }

            entries.Add(new ModelCreateEntry(
                contentType,
                string.Empty,
                RolesByContentType[contentType],
                name,
                spec.Description?.Trim() ?? string.Empty,
                path,
                Path.GetFileName(path),
                bytes.LongLength,
                items));
        }

        // Protection against substitution: create only makes new items. A model with the same name in the folder
        // is a model whose new revision is released by upload.
        var donors = new Dictionary<string, ModelDonor>(StringComparer.OrdinalIgnoreCase);
        var resolved = new List<ModelCreateEntry>();

        foreach (var group in entries.GroupBy(entry => entry.ContentType, StringComparer.OrdinalIgnoreCase))
        {
            var (inFolder, _) = await _components.SearchAsync(
                new ComponentCriteria { ContentType = group.Key, Folder = folder.Path, Limit = 5000 }, cancellationToken);

            foreach (ModelCreateEntry entry in group)
            {
                ComponentRecord? twin = inFolder.FirstOrDefault(record =>
                    string.Equals(record.FolderPath, folder.Path, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(record.Comment?.Trim(), entry.Name, StringComparison.OrdinalIgnoreCase));

                if (twin is not null)
                {
                    throw new InvalidOperationException(
                        $"Folder '{folder.Path}' already has {twin.Hrid} with the same name (comment) '{entry.Name}'. "
                        + "A new revision of this model is released by vault_model_files upload; if a separate model is needed — "
                        + "give another name (name).");
                }
            }

            var (samples, _) = await _components.SearchAsync(
                new ComponentCriteria { ContentType = group.Key, Limit = 1 }, cancellationToken);

            ComponentRecord sample = samples.FirstOrDefault()
                ?? throw new InvalidOperationException(
                    $"The vault has no item of type {group.Key} — there is nobody to borrow the service fields from "
                    + "(lifecycle, revision scheme). Create the first model of this type in Altium Designer.");

            var items = await _gateway.GetItemsAsync(
                VaultFilter.Equal("GUID", sample.ItemGuid), limit: 1, cancellationToken: cancellationToken);
            ALU_Item item = items.Count == 1
                ? items[0]
                : throw new InvalidOperationException($"Sample {sample.Hrid} was not read.");

            donors[group.Key] = new ModelDonor(sample.Hrid, item, sample.LatestRevision);

            resolved.AddRange(group.Select(entry => entry with { ContentTypeGuid = item.ContentTypeGUID }));
        }

        ComponentRecord? target = null;

        if (!string.IsNullOrWhiteSpace(component))
        {
            var found = await _components.ReadByIdsAsync([component.Trim()], cancellationToken);
            target = found.Count == 1
                ? found[0]
                : throw new InvalidOperationException(await _components.DescribeMissingAsync([component.Trim()], cancellationToken));

            TemplateService.EnsureNoTemplates([target]);

            if (target.ContentType is not null && RolesByContentType.ContainsKey(target.ContentType))
            {
                throw new InvalidOperationException(
                    $"{target.Hrid} is a model itself ({target.ContentType}), not a component: a model can only be linked to a part.");
            }

            var roles = resolved.GroupBy(entry => entry.Role, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).ToList();

            if (roles.Count > 0)
            {
                throw new InvalidOperationException(
                    $"One part gets one model of each role, but the call has several files of the role '{LinkRole.Describe(roles[0].Key)}'. "
                    + "Remove the extra files or create them without component.");
            }
        }

        return new ModelCreatePlan(folder, scheme, resolved, donors, target, notes);
    }

    /// <summary>
    /// Creates new items: one per file, with the first revision holding the library file. It never
    /// writes into an existing item.
    /// </summary>
    public async Task<ModelCreateOutcome> ApplyCreateAsync(
        ModelCreatePlan plan,
        string releaseNote,
        CancellationToken cancellationToken)
    {
        var created = new List<CreatedModel>();

        try
        {
            foreach (var group in plan.Entries.GroupBy(entry => entry.ContentType, StringComparer.OrdinalIgnoreCase))
            {
                ModelDonor donor = plan.Donors[group.Key];
                var entries = group.ToList();

                // Identifiers are issued by the server by the folder naming scheme.
                var hrids = await _gateway.GenerateItemHridsAsync(
                    plan.Folder.Guid, donor.Item.ContentTypeGUID, entries.Count, cancellationToken);

                if (hrids.Count < entries.Count)
                {
                    throw new InvalidOperationException(
                        $"The server issued {hrids.Count} identifiers instead of {entries.Count}. Check the folder naming scheme (vault_folder).");
                }

                for (int index = 0; index < entries.Count; index++)
                {
                    created.Add(await CreateOneAsync(plan, donor, entries[index], hrids[index], releaseNote, cancellationToken));
                }
            }
        }
        catch (Exception exception) when (created.Count > 0 && exception is not OperationCanceledException)
        {
            return new ModelCreateOutcome(created, exception.Message);
        }

        return new ModelCreateOutcome(created, null);
    }

    private async Task<CreatedModel> CreateOneAsync(
        ModelCreatePlan plan,
        ModelDonor donor,
        ModelCreateEntry entry,
        string hrid,
        string releaseNote,
        CancellationToken cancellationToken)
    {
        string itemGuid = NewGuid();
        string revisionGuid = NewGuid();

        // As with models released by Altium itself (checked against such models): rev. 1 without parameters and without an ancestor,
        // comment is the model name, ItemDescription is empty.
        var revision = new ALU_ItemRevision
        {
            GUID = revisionGuid,
            ItemGUID = itemGuid,
            RevisionId = "1",
            RevisionIdLevels = new _StringList { "1" },
            RevisionIdSeparators = donor.Revision?.RevisionIdSeparators ?? new _StringList { string.Empty },
            AncestorItemRevisionGUID = string.Empty,
            ContentTypeGUID = donor.Item.ContentTypeGUID,
            FolderGUID = plan.Folder.Guid,
            LifeCycleStateGUID = donor.Revision?.LifeCycleStateGUID,
            Comment = entry.Name,
            Description = entry.Description,
            ItemHRID = hrid,
            ItemDescription = string.Empty,
            IsVisible = true,
            IsApplicable = true,
            IsActive = true,
            IsShared = true,
            RevisionParameters = new _ALU_ItemRevisionParameterList(),
        };

        var item = new ALU_Item
        {
            GUID = itemGuid,
            HRID = hrid,
            Description = entry.Description,
            FolderGUID = plan.Folder.Guid,
            ContentTypeGUID = donor.Item.ContentTypeGUID,
            LifeCycleDefinitionGUID = donor.Item.LifeCycleDefinitionGUID,
            RevisionNamingSchemeGUID = donor.Item.RevisionNamingSchemeGUID,
            IsActive = true,
            Revisions = new _ALU_ItemRevisionList { revision },
        };

        await _gateway.AddItemsAsync([item], cancellationToken);
        await _scripts.ReleaseRevisionsAsync(
            [new RevisionRelease(revision, [], [new ReleaseFile(entry.FileName, entry.SourcePath)])],
            releaseNote,
            cancellationToken);

        return new CreatedModel(
            hrid, itemGuid, revisionGuid, entry.Role, entry.ContentType, entry.Name, entry.FileName, entry.Size, donor.Hrid);
    }

    /// <summary>
    /// Links the created models to a part by one revision — the same way as vault_set_links.
    /// </summary>
    /// <param name="footprintMode">
    /// What to do with the new footprint: replace the primary (default) or add as an additional one.
    /// On the symbol it has no effect.
    /// </param>
    public async Task<RevisionChangeResult> LinkCreatedAsync(
        ComponentRecord component,
        IReadOnlyList<CreatedModel> models,
        string releaseNote,
        CancellationToken cancellationToken,
        FootprintMode footprintMode = FootprintMode.Replace)
    {
        ALU_ItemRevision latest = component.LatestRevision
            ?? throw new InvalidOperationException($"{component.Hrid} has no revision that can be changed.");

        BatchResult result = await _batch.ApplyAsync(
            [new BatchEdit(latest, component.Hrid, new ComponentChange
            {
                Links = models.Select(model => new LinkAssignment(
                    model.Role,
                    model.RevisionGuid,
                    Append: footprintMode == FootprintMode.Add
                        && string.Equals(model.Role, LinkRole.Footprint, StringComparison.OrdinalIgnoreCase))).ToList(),
            })],
            releaseNote,
            RevisionBatch.DefaultChunkSize,
            progress: null,
            cancellationToken);

        if (result.Failures.Count > 0)
        {
            throw new InvalidOperationException(result.Failures[0].Error);
        }

        return result.Applied.Count == 1
            ? result.Applied[0]
            : throw new InvalidOperationException($"{component.Hrid}: the revision with the new links was not released.");
    }

    /// <summary>
    /// The naming scheme of the folder or the nearest ancestor. No scheme anywhere — a refusal before writing: without it the server
    /// will not issue an identifier, and an item without an identifier is not needed.
    /// </summary>
    private async Task<string> FindNamingSchemeAsync(FolderNode folder, CancellationToken cancellationToken)
    {
        var folders = await _catalog.GetFoldersAsync(cancellationToken);
        FolderNode? node = folder;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (node is not null && seen.Add(node.Guid))
        {
            var records = await _gateway.GetFoldersAsync(
                VaultFilter.Equal("GUID", node.Guid),
                VaultRequestOptions.Of(VaultRequestOptions.IncludeFolderParameters, VaultRequestOptions.IncludeSystemFolders),
                limit: 1,
                cancellationToken: cancellationToken);

            string? scheme = records.Count == 1
                ? records[0].FolderParameters
                    ?.FirstOrDefault(parameter => string.Equals(parameter.HRID, NamingSchemeParameter, StringComparison.OrdinalIgnoreCase))
                    ?.DefaultValue
                : null;

            if (!string.IsNullOrWhiteSpace(scheme))
            {
                return scheme;
            }

            node = node.ParentGuid is { Length: > 0 } parent && folders.TryGetValue(parent, out FolderNode? up) ? up : null;
        }

        throw new InvalidOperationException(
            $"Folder '{folder.Path}' and its parents have no naming scheme, so the server will not issue an identifier for the model. "
            + "Set a scheme on the folder (vault_folder create with namingScheme for a new folder) or choose a models folder "
            + "where a scheme already exists (vault_folders).");
    }
}
