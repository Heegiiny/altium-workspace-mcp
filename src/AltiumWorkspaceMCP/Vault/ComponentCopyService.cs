using AltiumWorkspaceMCP.Soap.Vault;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>Where the component type of the copy came from.</summary>
public static class TypeSource
{
    /// <summary>The type is named in the request.</summary>
    public const string Explicit = "explicit";

    /// <summary>The type of the component template linked to the copy (from the .cmpt file).</summary>
    public const string Template = "template";

    /// <summary>The type of the default template of the target folder.</summary>
    public const string FolderTemplate = "folderTemplate";

    /// <summary>The type of the sample.</summary>
    public const string Source = "source";

    /// <summary>No type: the sample had none and the request does not name one.</summary>
    public const string None = "none";
}

/// <summary>The component type of the copy and where it came from.</summary>
/// <param name="Path">The type path in the tree; empty if there is no type.</param>
/// <param name="Source">See <see cref="TypeSource"/>.</param>
/// <param name="Warnings">Disagreements between type sources and other remarks.</param>
public sealed record CopyType(string? Path, string Source)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>A created component copy.</summary>
/// <param name="Parameters">All parameters of the copy.</param>
/// <param name="ChangedParameters">Parameters in which the copy differs from the sample.</param>
/// <param name="Models">Models of the copy: role → model identifier.</param>
public sealed record ComponentCopy(
    string Hrid,
    string ItemGuid,
    string RevisionId,
    string RevisionGuid,
    string FolderPath,
    IReadOnlyDictionary<string, string> Parameters)
{
    public string? Comment { get; init; }

    public string? Description { get; init; }

    public CopyType? ComponentType { get; init; }

    public IReadOnlyDictionary<string, string> Models { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> ChangedParameters { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Instruction to create one copy.</summary>
/// <param name="Parameters">Parameter values that differ from the sample.</param>
/// <param name="Description">Copy description; empty — as in the sample.</param>
/// <param name="Comment">Copy name (the Comment column); empty — as in the sample.</param>
/// <param name="Links">Links that differ from the sample (role → target revision; an empty target removes the link).</param>
/// <param name="ComponentType">Component type: name, path or GUID; empty — the sample's type.</param>
public sealed record CopySpec(
    IReadOnlyDictionary<string, string> Parameters,
    string? Description,
    string? Comment = null,
    IReadOnlyList<LinkAssignment>? Links = null,
    string? ComponentType = null);

/// <summary>Copy result: what was created and where it broke, if it did.</summary>
/// <param name="Created">Created copies (each one in full or not created at all).</param>
/// <param name="FailedAt">Number of the copy (from 1) at which the failure happened.</param>
/// <param name="Error">Cause of the failure.</param>
public sealed record CopyOutcome(IReadOnlyList<ComponentCopy> Created, int? FailedAt, string? Error);

/// <summary>
/// Copying components — the same as "Copy" in the Explorer panel, including
/// creating a whole series of components of one kind at once.
/// </summary>
/// <remarks>
/// A copy gets its own item with an identifier issued by the server, the first
/// revision with the sample's parameters, its links to the symbol, footprint and template,
/// links to datasheets and the component type. All this is one atomic script group
/// per copy, as in Single Component Editor: the item, release, datasheet links and type
/// are created together or not at all, no extra revisions appear. A series of values
/// is created by one operation: the sample sets the layout and models, and the list of values — what
/// the copies differ in. The copy's parameters and links are written in the Altium format even if
/// the sample lacked something.
/// </remarks>
public sealed class ComponentCopyService
{
    /// <summary>A copy never deletes the sample's parameters — it only adds differences.</summary>
    private static readonly IReadOnlySet<string> NoDeletes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly VaultGateway _gateway;
    private readonly VaultCatalog _catalog;
    private readonly ComponentService _components;
    private readonly VaultScriptExecutor _scripts;
    private readonly LinkNormalizer _links;
    private readonly ParameterTypeResolver _types;
    private readonly ComponentTypeService _componentTypes;
    private readonly TemplateService _templates;

    public ComponentCopyService(
        VaultGateway gateway,
        VaultCatalog catalog,
        ComponentService components,
        VaultScriptExecutor scripts,
        LinkNormalizer links,
        ParameterTypeResolver types,
        ComponentTypeService componentTypes,
        TemplateService templates)
    {
        _gateway = gateway;
        _catalog = catalog;
        _components = components;
        _scripts = scripts;
        _links = links;
        _types = types;
        _componentTypes = componentTypes;
        _templates = templates;
    }

    /// <summary>
    /// Creates copies of the sample.
    /// </summary>
    /// <param name="source">Identifier or GUID of the sample component.</param>
    /// <param name="targetFolder">Folder for the copies; empty — the sample's folder.</param>
    /// <param name="specs">One record per copy.</param>
    public async Task<CopyOutcome> CopyAsync(
        string source,
        string? targetFolder,
        IReadOnlyList<CopySpec> specs,
        string releaseNote,
        CancellationToken cancellationToken)
    {
        if (specs.Count == 0)
        {
            throw new ArgumentException("No copies specified.", nameof(specs));
        }

        var found = await _components.ReadByIdsAsync([source], cancellationToken);
        ComponentRecord original = found.Count == 1
            ? found[0]
            : throw new InvalidOperationException(
                "Copy sample not found. " + await _components.DescribeMissingAsync([source], cancellationToken));

        ALU_ItemRevision sourceRevision = original.LatestRevision
            ?? throw new InvalidOperationException($"Component '{source}' has no revision that can be copied.");

        FolderNode folder = string.IsNullOrWhiteSpace(targetFolder)
            ? await _catalog.ResolveFolderAsync(original.FolderGuid, cancellationToken)
            : await _catalog.ResolveFolderAsync(targetFolder, cancellationToken);

        var newTypes = await _types.ResolveNewAsync(
            sourceRevision, specs.SelectMany(spec => spec.Parameters.Keys), cancellationToken);

        // The values of all copies are checked before the first one is created: otherwise a bad value
        // in the middle of a series would leave it half created.
        foreach (CopySpec spec in specs)
        {
            RevisionService.MergeParameters(
                sourceRevision, spec.Parameters, NoDeletes, sourceRevision.GUID, reuseGuids: false, newTypes,
                corrections: null);
        }

        var sourceItem = await LoadItemAsync(original.ItemGuid, cancellationToken);
        var sourceLinks = await _gateway.GetItemRevisionLinksAsync(
            VaultFilter.Equal("ParentItemRevisionGUID", sourceRevision.GUID),
            cancellationToken: cancellationToken);

        // The sample's links to datasheets are between items, not revisions: the copy
        // references the same datasheet items and creates no new ones (as in Altium).
        var sourceItemLinks = await _gateway.GetItemLinksAsync(
            VaultFilter.Equal("ParentItemGUID", original.ItemGuid),
            cancellationToken: cancellationToken);

        var revisionInfo = await ResolveRevisionsAsync(sourceLinks, specs, cancellationToken);
        var modelNames = revisionInfo.ToDictionary(
            entry => entry.Key, entry => entry.Value.ItemHrid, StringComparer.OrdinalIgnoreCase);

        var types = await ResolveTypesAsync(original, specs, sourceLinks, revisionInfo, folder, cancellationToken);

        // Identifiers are issued by the server: it tracks uniqueness and continues
        // the numbering used in the folder.
        var hrids = await _gateway.GenerateItemHridsAsync(
            folder.Guid, sourceItem.ContentTypeGUID, specs.Count, cancellationToken);

        if (hrids.Count < specs.Count)
        {
            throw new InvalidOperationException(
                $"The server issued {hrids.Count} identifiers instead of the requested {specs.Count}.");
        }

        var created = new List<ComponentCopy>(specs.Count);

        for (int index = 0; index < specs.Count; index++)
        {
            try
            {
                created.Add(await CreateCopyAsync(
                    original, sourceItem, sourceRevision, sourceLinks, sourceItemLinks, folder, hrids[index],
                    specs[index], newTypes, types[index], modelNames, releaseNote, cancellationToken));
            }
            catch (Exception exception) when (created.Count > 0 && exception is not OperationCanceledException)
            {
                // Each copy is its own atomic group: the ones created earlier stay whole, and they
                // must be reported, not lost together with the error.
                return new CopyOutcome(created, index + 1, exception.Message);
            }
        }

        return new CopyOutcome(created, null, null);
    }

    /// <summary>
    /// The type of each copy, in descending priority: named in the request
    /// (<see cref="TypeSource.Explicit"/>), set by the template linked to the copy
    /// (<see cref="TypeSource.Template"/>), set by the default template of the target folder
    /// (<see cref="TypeSource.FolderTemplate"/>), taken from the sample (<see cref="TypeSource.Source"/>).
    /// Checked before the first copy is created; a disagreement of sources is a warning.
    /// </summary>
    /// <remarks>
    /// The template type is read from its <c>.cmpt</c> file — as Altium does — and the revision parameter
    /// <c>ComponentTypeGuid</c> serves as the fallback source.
    /// </remarks>
    private async Task<IReadOnlyList<(ComponentTypeNode? Node, string Source, IReadOnlyList<string> Warnings)>> ResolveTypesAsync(
        ComponentRecord original,
        IReadOnlyList<CopySpec> specs,
        IReadOnlyList<ALU_ItemRevisionLink> sourceLinks,
        IReadOnlyDictionary<string, (string ItemGuid, string ItemHrid)> revisionInfo,
        FolderNode folder,
        CancellationToken cancellationToken)
    {
        var nodes = (await _componentTypes.ListAsync(cancellationToken))
            .ToDictionary(node => node.Guid, StringComparer.OrdinalIgnoreCase);

        bool needsDefaults = specs.Any(spec => string.IsNullOrWhiteSpace(spec.ComponentType));

        var templates = needsDefaults ? await _templates.ListAsync(cancellationToken) : [];
        var templateTypes = new Dictionary<string, TemplateType>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        async Task<TemplateType> TypeOfAsync(ComponentTemplate template)
        {
            if (!templateTypes.TryGetValue(template.ItemGuid, out TemplateType? type))
            {
                type = templateTypes[template.ItemGuid] = await _templates.ReadTypeAsync(template, cancellationToken);
            }

            return type;
        }

        // The default template of the target folder.
        TypeCandidate folderCandidate = new(TypeSource.FolderTemplate, null);
        string folderTemplateName = string.Empty;

        if (needsDefaults && await _templates.GetFolderTemplateAsync(folder.Guid, cancellationToken) is { } folderTemplate)
        {
            folderCandidate = new(TypeSource.FolderTemplate, (await TypeOfAsync(folderTemplate)).TypeGuid);
            folderTemplateName = folderTemplate.Hrid;
        }

        // The type of the sample.
        ComponentTypeNode? sourceNode = null;

        if (needsDefaults)
        {
            var assigned = await _componentTypes.GetAssignedAsync([original.ItemGuid], cancellationToken);
            if (assigned.TryGetValue(original.ItemGuid, out string? path))
            {
                sourceNode = await _componentTypes.ResolveAsync(path, cancellationToken);
            }
        }

        var result = new List<(ComponentTypeNode?, string, IReadOnlyList<string>)>(specs.Count);
        var explicitCache = new Dictionary<string, ComponentTypeNode>(StringComparer.OrdinalIgnoreCase);

        foreach (CopySpec spec in specs)
        {
            var copyWarnings = new List<string>();

            if (!string.IsNullOrWhiteSpace(spec.ComponentType))
            {
                if (!explicitCache.TryGetValue(spec.ComponentType, out ComponentTypeNode? node))
                {
                    node = explicitCache[spec.ComponentType] =
                        await _componentTypes.ResolveAsync(spec.ComponentType, cancellationToken);
                }

                result.Add((node, TypeSource.Explicit, copyWarnings));
                continue;
            }

            // The template the copy references: from the request or the same as the sample's.
            string? templateRevision = spec.Links?
                    .FirstOrDefault(link => string.Equals(link.Role, LinkRole.Template, StringComparison.OrdinalIgnoreCase))
                    is { } replaced
                ? replaced.TargetRevisionGuid
                : sourceLinks
                    .FirstOrDefault(link => string.Equals(link.HRID, LinkRole.Template, StringComparison.OrdinalIgnoreCase))
                    ?.ChildItemRevisionGUID;

            ComponentTemplate? template = templateRevision is not null
                && revisionInfo.TryGetValue(templateRevision, out var info)
                    ? templates.FirstOrDefault(candidate => string.Equals(candidate.ItemGuid, info.ItemGuid, StringComparison.OrdinalIgnoreCase))
                    : null;

            TypeCandidate templateCandidate = new(TypeSource.Template, null);

            if (template is not null)
            {
                TemplateType templateType = await TypeOfAsync(template);
                templateCandidate = new(TypeSource.Template, templateType.TypeGuid);

                if (templateType.Disagree)
                {
                    copyWarnings.Add(
                        $"The type of template {template.Hrid} in the .cmpt file ({Name(nodes, templateType.FromFile)}) and in the parameter "
                        + $"ComponentTypeGuid ({Name(nodes, templateType.FromParameter)}) disagree; Altium reads the file, "
                        + "the type from the file was taken.");
                }
            }

            TypeChoice choice = ComponentTypeChooser.Choose(
            [
                templateCandidate,
                folderCandidate,
                new(TypeSource.Source, sourceNode?.Guid),
            ]);

            if (choice.Chosen is null)
            {
                result.Add((null, TypeSource.None, copyWarnings));
                continue;
            }

            foreach (TypeCandidate other in choice.Disagreeing)
            {
                copyWarnings.Add(
                    $"Type sources disagree: '{Name(nodes, choice.Chosen.TypeGuid)}' was chosen ({Describe(choice.Chosen.Source)}), "
                    + $"but {Describe(other.Source)} gives '{Name(nodes, other.TypeGuid)}'. "
                    + "To get another type, name it explicitly: componentType.");
            }

            result.Add((
                nodes.GetValueOrDefault(choice.Chosen.TypeGuid!),
                choice.Chosen.Source,
                copyWarnings));
        }

        return result;

        string Describe(string source) => source switch
        {
            TypeSource.Template => "the copy's template",
            TypeSource.FolderTemplate => folderTemplateName.Length > 0 ? $"the folder template {folderTemplateName}" : "the folder template",
            TypeSource.Source => "the sample",
            _ => source,
        };

        static string Name(IReadOnlyDictionary<string, ComponentTypeNode> nodes, string? guid) =>
            guid is not null && nodes.TryGetValue(guid, out ComponentTypeNode? node) ? node.Path : guid ?? "none";
    }

    /// <summary>
    /// Revisions referenced by the copies' links: revision GUID → (item, item identifier).
    /// They are used to name the models in the response, and the template is found by its own item.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, (string ItemGuid, string ItemHrid)>> ResolveRevisionsAsync(
        IReadOnlyList<ALU_ItemRevisionLink> sourceLinks,
        IReadOnlyList<CopySpec> specs,
        CancellationToken cancellationToken)
    {
        var guids = sourceLinks
            .Select(link => link.ChildItemRevisionGUID)
            .Concat(specs.SelectMany(spec => spec.Links ?? []).Select(assignment => assignment.TargetRevisionGuid))
            .Concat(specs.SelectMany(spec => spec.Links ?? []).SelectMany(assignment => assignment.TargetRevisionGuids ?? []))
            .Where(guid => !string.IsNullOrEmpty(guid))
            .Select(guid => guid!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (guids.Count == 0)
        {
            return new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        }

        var revisions = await VaultGateway.ReadInChunksAsync(
            guids,
            chunk => _gateway.GetItemRevisionsAsync(
                VaultFilter.In("GUID", chunk), limit: 100000, cancellationToken: cancellationToken));

        return revisions
            .Where(revision => !string.IsNullOrEmpty(revision.GUID))
            .GroupBy(revision => revision.GUID, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (group.First().ItemGUID ?? string.Empty, group.First().ItemHRID ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);
    }

    private async Task<ComponentCopy> CreateCopyAsync(
        ComponentRecord original,
        ALU_Item sourceItem,
        ALU_ItemRevision sourceRevision,
        IReadOnlyList<ALU_ItemRevisionLink> sourceLinks,
        IReadOnlyList<ALU_ItemLink> sourceItemLinks,
        FolderNode folder,
        string hrid,
        CopySpec spec,
        IReadOnlyDictionary<string, ParameterTypeInfo> newTypes,
        (ComponentTypeNode? Node, string Source, IReadOnlyList<string> Warnings) type,
        IReadOnlyDictionary<string, string> modelNames,
        string releaseNote,
        CancellationToken cancellationToken)
    {
        string itemGuid = NewGuid();
        string revisionGuid = NewGuid();
        string description = spec.Description ?? sourceRevision.Description ?? sourceItem.Description ?? string.Empty;

        // A copy starts its own history: the first revision has no ancestor.
        string revisionId = FirstRevisionId(sourceRevision);

        var revision = new ALU_ItemRevision
        {
            GUID = revisionGuid,
            ItemGUID = itemGuid,
            RevisionId = revisionId,
            RevisionIdLevels = SplitLevels(revisionId),
            RevisionIdSeparators = sourceRevision.RevisionIdSeparators ?? new _StringList(),
            ContentTypeGUID = sourceItem.ContentTypeGUID,
            FolderGUID = folder.Guid,
            LifeCycleStateGUID = sourceRevision.LifeCycleStateGUID,
            Description = description,
            Comment = spec.Comment ?? sourceRevision.Comment,
            ItemHRID = hrid,
            ItemDescription = description,
            IsVisible = true,
            IsApplicable = true,
            IsActive = true,
            RevisionParameters = RevisionService.MergeParameters(
                sourceRevision, spec.Parameters, NoDeletes, revisionGuid, reuseGuids: false, newTypes,
                corrections: null),
        };

        // The item is created at once with the first revision: without it the server answers
        // "Missing initial item revision" — an item without a revision is meaningless for it.
        var revisions = new _ALU_ItemRevisionList { revision };

        var item = new ALU_Item
        {
            GUID = itemGuid,
            HRID = hrid,
            Description = description,
            FolderGUID = folder.Guid,
            ContentTypeGUID = sourceItem.ContentTypeGUID,
            LifeCycleDefinitionGUID = sourceItem.LifeCycleDefinitionGUID,
            RevisionNamingSchemeGUID = sourceItem.RevisionNamingSchemeGUID,
            IsActive = true,
            Revisions = revisions,
        };

        // The sample's links with replacements from the request (for example, another footprint).
        var (links, _) = RevisionService.BuildLinks(
            sourceLinks, new ComponentChange { Links = spec.Links ?? [] }, revisionGuid);

        // The copy's links must be in the Altium format, otherwise it will see
        // neither the symbol nor the footprint.
        await _links.NormalizeAsync(links, cancellationToken);

        var itemLinks = sourceItemLinks.Select(link => CopyItemLink(link, itemGuid)).ToList();

        var tags = type.Node is null
            ? []
            : new List<ALU_ItemTag>
            {
                new() { GUID = string.Empty, ItemGUID = itemGuid, TagGUID = type.Node.Guid },
            };

        // One atomic group: the item, release, datasheets and type are created together or not at all.
        await _scripts.CreateComponentAsync(
            new ComponentCreation(item, links, itemLinks, tags), releaseNote, cancellationToken);

        var parameters = ComponentService.ExtractParameters(revision);

        var changed = parameters
            .Where(entry => !original.Parameters.TryGetValue(entry.Key, out string? previous)
                || !string.Equals(previous, entry.Value, StringComparison.Ordinal))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);

        var models = links
            .Where(link => !string.IsNullOrEmpty(link.ChildItemRevisionGUID))
            .GroupBy(link => LinkRole.Describe(link.HRID))
            .ToDictionary(
                group => group.Key,
                group => modelNames.GetValueOrDefault(group.First().ChildItemRevisionGUID ?? string.Empty)
                    ?? group.First().ChildItemRevisionGUID!,
                StringComparer.OrdinalIgnoreCase);

        return new ComponentCopy(hrid, itemGuid, revisionId, revisionGuid, folder.Path, parameters)
        {
            Comment = revision.Comment,
            Description = description,
            ComponentType = new CopyType(type.Node?.Path, type.Source) { Warnings = type.Warnings },
            Models = models,
            ChangedParameters = changed,
        };
    }

    /// <summary>
    /// A copy's link to the same datasheet item. GUIDs are empty — assigned by the server, as in Altium;
    /// the link parameters (file name, hash, size, signature) have neither their own GUIDs nor a reference to the old link.
    /// </summary>
    private static ALU_ItemLink CopyItemLink(ALU_ItemLink source, string parentItemGuid)
    {
        var parameters = new _ALU_ItemLinkParameterList();

        foreach (ALU_ItemLinkParameter parameter in source.LinkParameters ?? [])
        {
            parameters.Add(new ALU_ItemLinkParameter
            {
                GUID = string.Empty,
                HRID = parameter.HRID,
                ParameterValue = parameter.ParameterValue,
                ItemLinkGUID = string.Empty,
            });
        }

        return new ALU_ItemLink
        {
            GUID = string.Empty,
            HRID = string.Empty,
            ParentItemGUID = parentItemGuid,
            ParentVaultGUID = source.ParentVaultGUID,
            ChildItemGUID = source.ChildItemGUID,
            ChildVaultGUID = source.ChildVaultGUID,
            LinkTypeGUID = source.LinkTypeGUID,
            LinkParameters = parameters,
        };
    }

    private async Task<ALU_Item> LoadItemAsync(string itemGuid, CancellationToken cancellationToken)
    {
        var items = await _gateway.GetItemsAsync(
            VaultFilter.Equal("GUID", itemGuid), limit: 1, cancellationToken: cancellationToken);

        return items.Count == 1
            ? items[0]
            : throw new InvalidOperationException($"Item {itemGuid} not found.");
    }

    /// <summary>The first revision of the copy in the same numbering as the sample's.</summary>
    private static string FirstRevisionId(ALU_ItemRevision source)
    {
        var levels = (source.RevisionIdLevels ?? []).Where(level => !string.IsNullOrEmpty(level)).ToList();

        if (levels.Count == 0)
        {
            return "1";
        }

        // Higher levels are kept, the lower one starts over.
        levels[^1] = 1.ToString(new string('0', levels[^1].Length));

        string separator = source.RevisionIdSeparators?.FirstOrDefault() ?? "-";
        return string.Join(separator, levels);
    }

    private static _StringList SplitLevels(string revisionId)
    {
        var list = new _StringList();
        list.AddRange(revisionId.Split('-', StringSplitOptions.RemoveEmptyEntries));
        return list;
    }

    private static string NewGuid() => Guid.NewGuid().ToString("D").ToUpperInvariant();
}
