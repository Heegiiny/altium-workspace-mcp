using AltiumWorkspaceMCP.Soap.Vault;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>One found mismatch with the Altium Designer format.</summary>
public sealed record ComponentIssue(string Kind, string Detail, bool Fixable)
{
    /// <summary>The parameter the mismatch refers to.</summary>
    public string? Parameter { get; init; }

    /// <summary>The value the fix will return — for values cleared on save.</summary>
    public string? RestoreValue { get; init; }

    /// <summary>The component type that will be assigned (only for <see cref="ComponentHealthService.Kinds.MissingComponentType"/>).</summary>
    public string? TypeGuid { get; init; }

    /// <summary>Path of the type to assign in the type tree.</summary>
    public string? TypePath { get; init; }
}

/// <summary>Result of checking one component.</summary>
public sealed record ComponentHealth(ComponentRecord Record, IReadOnlyList<ComponentIssue> Issues)
{
    public bool HasIssues => Issues.Count > 0;

    public bool IsFixable => Issues.Any(issue => issue.Fixable);

    /// <summary>There is a fixable item that needs a new revision (everything except the component type).</summary>
    public bool NeedsRevisionFix => Issues.Any(issue => issue.Fixable && !IsTypeIssue(issue));

    /// <summary>The component type that can be assigned without a new revision.</summary>
    public ComponentIssue? TypeFix =>
        Issues.FirstOrDefault(issue => IsTypeIssue(issue) && issue.Fixable && issue.TypeGuid is not null);

    private static bool IsTypeIssue(ComponentIssue issue) =>
        issue.Kind == ComponentHealthService.Kinds.MissingComponentType;

    /// <summary>
    /// The edit that fixes what was found. It contains only the values to return:
    /// parameter numbers and link flags are appended when any new revision is released.
    /// </summary>
    public ComponentChange FixChange => new()
    {
        Parameters = Issues
            .Where(issue => issue.Fixable && issue.Parameter is not null && issue.RestoreValue is not null)
            .GroupBy(issue => issue.Parameter!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().RestoreValue!, StringComparer.OrdinalIgnoreCase),
    };
}

/// <summary>
/// Checking components so that Altium Designer reads them correctly and does not damage them
/// on save.
/// </summary>
/// <remarks>
/// The vault server accepts data that Altium Designer does not fully understand.
/// The check looks for exactly such places: a typed parameter without a numeric value
/// (Altium clears it on saving the component), a footprint link without a
/// number (Altium will not show the footprint), a link without a vault GUID or role.
/// The revision history also finds values that were already cleared: in an earlier revision the value
/// was there, but without a number, and in the next one it became empty — exactly how Single Component Editor
/// treats such a parameter on save.
///
/// Everything fixable is fixed by a new revision in which the missing is appended by
/// Altium rules and the cleared values are returned from history.
/// </remarks>
public sealed class ComponentHealthService
{
    /// <summary>Kinds of mismatches.</summary>
    public static class Kinds
    {
        public const string MissingNumber = "typed parameter has no number";
        public const string StaleNumber = "number does not match the value";
        public const string InvalidValue = "value does not parse";
        public const string ClearedValue = "value cleared on save in Altium";
        public const string FootprintData = "footprint link has no FootprintIndex";

        /// <summary>
        /// The footprint set is broken: two primaries, a repeated number, a "PCBLIB n" role with FootprintIndex other than n.
        /// The check fixes nothing: which footprint is primary is the owner's decision (vault_set_links, the footprints role).
        /// </summary>
        public const string FootprintSet = "footprint set is broken";
        public const string VaultGuid = "link has no vault GUID";
        public const string UnknownRole = "link has a GUID instead of a role";
        public const string MissingComponentType = "part has no component type";

        /// <summary>The active revision references a model that is now in the trash.</summary>
        public const string ModelInTrash = "reference to a model in the trash";
    }

    /// <summary>Only components have a component type; symbols, footprints and templates do not.</summary>
    private const string ComponentContentType = "altium-component";

    private readonly VaultGateway _gateway;
    private readonly ComponentTypeService _componentTypes;
    private readonly TemplateService _templates;

    public ComponentHealthService(VaultGateway gateway, ComponentTypeService componentTypes, TemplateService templates)
    {
        _gateway = gateway;
        _componentTypes = componentTypes;
        _templates = templates;
    }

    /// <summary>Checks components; links and history are read by shared selections.</summary>
    /// <param name="includeHistory">Look for values already cleared on save in Altium.</param>
    public async Task<IReadOnlyList<ComponentHealth>> InspectAsync(
        IReadOnlyList<ComponentRecord> records,
        bool includeHistory,
        CancellationToken cancellationToken)
    {
        var revisionGuids = records
            .Select(record => record.RevisionGuid)
            .Where(guid => !string.IsNullOrEmpty(guid))
            .Select(guid => guid!)
            .ToList();

        var links = await VaultGateway.ReadInChunksAsync(
            revisionGuids,
            chunk => _gateway.GetItemRevisionLinksAsync(
                VaultFilter.In("ParentItemRevisionGUID", chunk), limit: 100000,
                cancellationToken: cancellationToken));

        var linksByRevision = links
            .GroupBy(link => link.ParentItemRevisionGUID ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var historyByItem = new Dictionary<string, List<ALU_ItemRevision>>(StringComparer.OrdinalIgnoreCase);

        if (includeHistory)
        {
            var revisions = await VaultGateway.ReadInChunksAsync(
                records.Select(record => record.ItemGuid).Distinct(StringComparer.OrdinalIgnoreCase),
                chunk => _gateway.GetItemRevisionsAsync(
                    VaultFilter.In("ItemGUID", chunk),
                    VaultRequestOptions.Of(VaultRequestOptions.IncludeItemRevisionParameters),
                    limit: 100000,
                    cancellationToken: cancellationToken));

            historyByItem = revisions
                .GroupBy(revision => revision.ItemGUID ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderBy(revision => revision.CreatedAt).ThenBy(revision => revision.RevisionId).ToList(),
                    StringComparer.OrdinalIgnoreCase);
        }

        var typeIssues = await InspectTypesAsync(records, linksByRevision, cancellationToken);
        var deletedModelIssues = await InspectDeletedModelsAsync(records, linksByRevision, cancellationToken);

        return records
            .Select(record =>
            {
                var issues = Inspect(
                    record,
                    linksByRevision.GetValueOrDefault(record.RevisionGuid ?? string.Empty, []),
                    historyByItem.GetValueOrDefault(record.ItemGuid, []));

                if (typeIssues.TryGetValue(record.ItemGuid, out ComponentIssue? typeIssue))
                {
                    issues = [.. issues, typeIssue];
                }

                if (deletedModelIssues.TryGetValue(record.ItemGuid, out List<ComponentIssue>? trashIssues))
                {
                    issues = [.. issues, .. trashIssues];
                }

                return new ComponentHealth(record, issues);
            })
            .ToList();
    }

    /// <summary>
    /// Links of the active revision that lead to a model in the trash: the link
    /// is intact, but the item it points to is deleted. A new revision does not fix it — the model
    /// must be restored first (vault_restore_items), so <c>Fixable: false</c>.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, List<ComponentIssue>>> InspectDeletedModelsAsync(
        IReadOnlyList<ComponentRecord> records,
        IReadOnlyDictionary<string, List<ALU_ItemRevisionLink>> linksByRevision,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, List<ComponentIssue>>(StringComparer.OrdinalIgnoreCase);

        var childRevisionGuids = records
            .SelectMany(record => linksByRevision.GetValueOrDefault(record.RevisionGuid ?? string.Empty, []))
            .Select(link => link.ChildItemRevisionGUID)
            .Where(guid => !string.IsNullOrEmpty(guid))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (childRevisionGuids.Count == 0)
        {
            return result;
        }

        var childRevisions = await VaultGateway.ReadInChunksAsync(
            childRevisionGuids,
            chunk => _gateway.GetItemRevisionsAsync(
                VaultFilter.In("GUID", chunk), limit: 100000, cancellationToken: cancellationToken));

        var revisionToItem = childRevisions
            .Where(revision => !string.IsNullOrEmpty(revision.ItemGUID))
            .ToDictionary(revision => revision.GUID, revision => revision.ItemGUID!, StringComparer.OrdinalIgnoreCase);

        var itemGuids = revisionToItem.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (itemGuids.Count == 0)
        {
            return result;
        }

        var deletedItems = await VaultGateway.ReadInChunksAsync(
            itemGuids,
            chunk => _gateway.GetItemsAsync(
                VaultFilter.In("GUID", chunk),
                VaultRequestOptions.Of(VaultRequestOptions.IncludeDeletedOnly),
                limit: 100000,
                cancellationToken: cancellationToken));

        if (deletedItems.Count == 0)
        {
            return result;
        }

        var deletedByGuid = deletedItems
            .Where(item => !string.IsNullOrEmpty(item.GUID))
            .ToDictionary(item => item.GUID, StringComparer.OrdinalIgnoreCase);

        foreach (ComponentRecord record in records)
        {
            foreach (ALU_ItemRevisionLink link in linksByRevision.GetValueOrDefault(record.RevisionGuid ?? string.Empty, []))
            {
                if (link.ChildItemRevisionGUID is not { } childRevisionGuid
                    || !revisionToItem.TryGetValue(childRevisionGuid, out string? itemGuid)
                    || !deletedByGuid.TryGetValue(itemGuid, out ALU_Item? deletedItem))
                {
                    continue;
                }

                if (!result.TryGetValue(record.ItemGuid, out List<ComponentIssue>? list))
                {
                    result[record.ItemGuid] = list = [];
                }

                list.Add(DescribeDeletedModel(link.HRID, deletedItem.HRID ?? itemGuid, itemGuid));
            }
        }

        return result;
    }

    /// <summary>Text of the mismatch "reference to a model in the trash" with a hint on which command restores it.</summary>
    internal static ComponentIssue DescribeDeletedModel(string? role, string modelHrid, string modelItemGuid) =>
        new(
            Kinds.ModelInTrash,
            $"{LinkRole.Describe(role ?? string.Empty)}: '{modelHrid}' is in the trash. Restore: "
                + $"vault_restore_items action=restore itemGuids=[\"{modelItemGuid}\"].",
            Fixable: false)
        {
            Parameter = LinkRole.Describe(role ?? string.Empty),
        };

    /// <summary>
    /// Components without a type. The tags of all checked parts are read in portions of 1000; for
    /// each part without a type, a type is proposed from the same sources as on creation:
    /// the part's template, then the default template of its folder. Without a source there is nothing to fix with.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, ComponentIssue>> InspectTypesAsync(
        IReadOnlyList<ComponentRecord> records,
        IReadOnlyDictionary<string, List<ALU_ItemRevisionLink>> linksByRevision,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, ComponentIssue>(StringComparer.OrdinalIgnoreCase);

        var components = records
            .Where(record => string.Equals(record.ContentType, ComponentContentType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (components.Count == 0)
        {
            return result;
        }

        var assigned = await _componentTypes.GetAssignedAsync(
            components.Select(record => record.ItemGuid).ToList(), cancellationToken);

        var missing = components.Where(record => !assigned.ContainsKey(record.ItemGuid)).ToList();

        if (missing.Count == 0)
        {
            return result;
        }

        var nodes = (await _componentTypes.ListAsync(cancellationToken))
            .ToDictionary(node => node.Guid, StringComparer.OrdinalIgnoreCase);
        var templates = await _templates.ListAsync(cancellationToken);

        // The part's template — by the revision its ComponentTemplate link points to.
        var templateRevisionGuids = missing
            .SelectMany(record => linksByRevision.GetValueOrDefault(record.RevisionGuid ?? string.Empty, []))
            .Where(link => string.Equals(link.HRID, LinkRole.Template, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(link.ChildItemRevisionGUID))
            .Select(link => link.ChildItemRevisionGUID)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var revisionItems = templateRevisionGuids.Count == 0
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : (await VaultGateway.ReadInChunksAsync(
                    templateRevisionGuids,
                    chunk => _gateway.GetItemRevisionsAsync(
                        VaultFilter.In("GUID", chunk), limit: 100000, cancellationToken: cancellationToken)))
                .Where(revision => !string.IsNullOrEmpty(revision.GUID))
                .GroupBy(revision => revision.GUID, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().ItemGUID ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        var templateTypes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var folderTemplates = new Dictionary<string, ComponentTemplate?>(StringComparer.OrdinalIgnoreCase);

        async Task<string?> TypeOfAsync(ComponentTemplate template)
        {
            if (!templateTypes.TryGetValue(template.ItemGuid, out string? type))
            {
                type = templateTypes[template.ItemGuid] =
                    (await _templates.ReadTypeAsync(template, cancellationToken)).TypeGuid;
            }

            return type;
        }

        foreach (ComponentRecord record in missing)
        {
            var links = linksByRevision.GetValueOrDefault(record.RevisionGuid ?? string.Empty, []);

            string? templateRevision = links
                .FirstOrDefault(link => string.Equals(link.HRID, LinkRole.Template, StringComparison.OrdinalIgnoreCase))
                ?.ChildItemRevisionGUID;

            ComponentTemplate? template = templateRevision is not null
                && revisionItems.TryGetValue(templateRevision, out string? templateItem)
                    ? templates.FirstOrDefault(candidate => string.Equals(candidate.ItemGuid, templateItem, StringComparison.OrdinalIgnoreCase))
                    : null;

            if (!folderTemplates.TryGetValue(record.FolderGuid, out ComponentTemplate? folderTemplate))
            {
                folderTemplate = folderTemplates[record.FolderGuid] =
                    await _templates.GetFolderTemplateAsync(record.FolderGuid, cancellationToken);
            }

            TypeChoice choice = ComponentTypeChooser.Choose(
            [
                new TypeCandidate(TypeSource.Template, template is null ? null : await TypeOfAsync(template)),
                new TypeCandidate(TypeSource.FolderTemplate, folderTemplate is null ? null : await TypeOfAsync(folderTemplate)),
            ]);

            if (choice.Chosen is { TypeGuid: { } guid } && nodes.TryGetValue(guid, out ComponentTypeNode? node))
            {
                string via = choice.Chosen.Source == TypeSource.Template
                    ? $"the part's template {template?.Hrid}"
                    : $"the folder's template {folderTemplate?.Hrid}";

                result[record.ItemGuid] = new ComponentIssue(
                    Kinds.MissingComponentType,
                    $"no component type: the part is not visible in the Components panel tree; '{node.Path}' will be assigned ({via})",
                    Fixable: true)
                {
                    TypeGuid = node.Guid,
                    TypePath = node.Path,
                };
            }
            else
            {
                result[record.ItemGuid] = new ComponentIssue(
                    Kinds.MissingComponentType,
                    "no component type: the part is not visible in the Components panel tree, and there is nothing to determine the type from "
                        + "(the part has no template with a type, its folder has no default template). "
                        + "Assign the type manually: vault_component_types action=assign.",
                    Fixable: false);
            }
        }

        return result;
    }

    /// <summary>Checks one component from an already read revision, its links and history.</summary>
    public static IReadOnlyList<ComponentIssue> Inspect(
        ComponentRecord record,
        IReadOnlyList<ALU_ItemRevisionLink> links,
        IReadOnlyList<ALU_ItemRevision> history)
    {
        var issues = new List<ComponentIssue>();
        ALU_ItemRevision? revision = record.LatestRevision;

        if (revision is null)
        {
            return issues;
        }

        foreach (ALU_ItemRevisionParameter parameter in revision.RevisionParameters ?? [])
        {
            if (ParameterValueCodec.IsText(parameter.ParameterTypeGUID))
            {
                continue;
            }

            string name = parameter.HRID ?? string.Empty;

            if (string.IsNullOrWhiteSpace(parameter.ParameterValue))
            {
                if (FindCleared(revision, name, history) is { } cleared)
                {
                    issues.Add(cleared);
                }

                continue;
            }

            string type = ParameterValueCodec.DescribeType(parameter.ParameterTypeGUID);
            string? expected = ParameterValueCodec.RealValueFor(parameter.ParameterValue, parameter.ParameterTypeGUID);

            if (expected is null)
            {
                if (ParameterValueCodec.IsKnown(parameter.ParameterTypeGUID))
                {
                    issues.Add(new ComponentIssue(
                        Kinds.InvalidValue,
                        $"'{name}' = '{parameter.ParameterValue}' does not parse as {type} "
                            + $"({ParameterValueCodec.UnitOf(parameter.ParameterTypeGUID)}); Altium will clear it on save. "
                            + "Fix the value with a parameter edit.",
                        Fixable: false) { Parameter = name });
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(parameter.ParameterRealValue))
            {
                issues.Add(new ComponentIssue(
                    Kinds.MissingNumber,
                    $"'{name}' = '{parameter.ParameterValue}' ({type}): the number {expected} will be appended",
                    Fixable: true) { Parameter = name });
            }
            else if (!string.Equals(parameter.ParameterRealValue, expected, StringComparison.Ordinal))
            {
                issues.Add(new ComponentIssue(
                    Kinds.StaleNumber,
                    $"'{name}' = '{parameter.ParameterValue}', recorded {parameter.ParameterRealValue}, should be {expected}",
                    Fixable: true) { Parameter = name });
            }
        }

        foreach (ALU_ItemRevisionLink link in links)
        {
            string role = link.HRID ?? string.Empty;

            if (LinkRole.IsFootprint(role)
                && !LinkNormalizer.TryReadFootprint(link.Data, out _, out _))
            {
                issues.Add(new ComponentIssue(
                    Kinds.FootprintData,
                    $"footprint {link.ChildItemRevisionGUID}: Altium will not show it",
                    Fixable: string.IsNullOrWhiteSpace(link.Data)));
            }

            if (string.IsNullOrWhiteSpace(link.ParentVaultGUID) || string.IsNullOrWhiteSpace(link.ChildVaultGUID))
            {
                issues.Add(new ComponentIssue(
                    Kinds.VaultGuid, $"link '{role}' to {link.ChildItemRevisionGUID}", Fixable: true));
            }

            // An empty role is written by older Altium versions, and Altium itself understands such links.
            // A GUID instead of a role is a trace of an old copy bug; on fix the role
            // is restored by the type of the object the link points to.
            if (Guid.TryParse(role, out _))
            {
                issues.Add(new ComponentIssue(
                    Kinds.UnknownRole, $"link to {link.ChildItemRevisionGUID} with HRID '{role}'", Fixable: true));
            }
        }

        foreach (FootprintSetViolation violation in FootprintLinks.Check(links))
        {
            issues.Add(new ComponentIssue(
                Kinds.FootprintSet,
                violation.Detail + ". Fix with a set: vault_set_links, the footprints role and a targets list (the first is primary).",
                Fixable: false));
        }

        return issues;
    }

    /// <summary>
    /// Looks in the history for a trace of clearing: the last non-empty value of the parameter was written
    /// without a number, and the next revision made it empty.
    /// </summary>
    private static ComponentIssue? FindCleared(
        ALU_ItemRevision latest,
        string name,
        IReadOnlyList<ALU_ItemRevision> history)
    {
        // The active revision from an item selection comes without an author, so
        // its instance from the history is taken, if it is there.
        ALU_ItemRevision current = history.FirstOrDefault(revision =>
            string.Equals(revision.GUID, latest.GUID, StringComparison.OrdinalIgnoreCase)) ?? latest;

        var ordered = history
            .Where(revision => !string.Equals(revision.GUID, latest.GUID, StringComparison.OrdinalIgnoreCase))
            .Append(current)
            .ToList();

        for (int index = ordered.Count - 2; index >= 0; index--)
        {
            ALU_ItemRevisionParameter? before = ParameterTypeResolver.Find(ordered[index], name);

            if (before is null || string.IsNullOrWhiteSpace(before.ParameterValue))
            {
                continue;
            }

            // Altium does not clear a value with a number: an empty value after it is someone's edit.
            if (!string.IsNullOrWhiteSpace(before.ParameterRealValue)
                || ParameterValueCodec.RealValueFor(before.ParameterValue, before.ParameterTypeGUID) is null)
            {
                return null;
            }

            ALU_ItemRevision clearing = ordered[index + 1];

            return new ComponentIssue(
                Kinds.ClearedValue,
                $"'{name}': in rev. {ordered[index].RevisionId} there was '{before.ParameterValue}' without a number, "
                    + $"in rev. {clearing.RevisionId} (saved by '{clearing.CreatedByName}') the value is empty; "
                    + $"'{before.ParameterValue}' will be returned",
                Fixable: true)
            {
                Parameter = name,
                RestoreValue = before.ParameterValue,
            };
        }

        return null;
    }
}
