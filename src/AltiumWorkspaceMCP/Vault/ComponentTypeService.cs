using AltiumWorkspaceMCP.Soap.Vault;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>A component type with its computed path in the type tree.</summary>
public sealed record ComponentTypeNode(
    string Guid,
    string Name,
    string Path,
    string? ParentGuid);

/// <summary>
/// Component types — those visible in Preferences → Data Management → Component Types
/// and by which a part is found in the Components panel.
/// </summary>
/// <remarks>
/// Altium stores them as tags of the system family
/// <see cref="VaultTags.ComponentTypeFamilyGuid"/>: the hierarchy is set by a tag's reference to
/// its parent, and a part's membership in a type — by assigning the tag to the item. Separately,
/// a component template references the type — by the parameter <c>ComponentTypeGuid</c>; it
/// determines the type for parts created from this template.
/// </remarks>
public sealed class ComponentTypeService
{
    private readonly VaultGateway _gateway;

    public ComponentTypeService(VaultGateway gateway) => _gateway = gateway;

    /// <summary>All component types with paths like "Passive\Resistors\Resistors Small".</summary>
    public async Task<IReadOnlyList<ComponentTypeNode>> ListAsync(CancellationToken cancellationToken)
    {
        var tags = await LoadTagsAsync(cancellationToken);
        var byGuid = tags.ToDictionary(tag => tag.GUID, StringComparer.OrdinalIgnoreCase);

        return tags
            .Select(tag => new ComponentTypeNode(
                tag.GUID,
                tag.HRID ?? string.Empty,
                BuildPath(tag, byGuid),
                string.IsNullOrEmpty(tag.ParentTagGUID) ? null : tag.ParentTagGUID))
            .OrderBy(node => node.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Finds a type by name, full path or GUID.</summary>
    public async Task<ComponentTypeNode> ResolveAsync(string type, CancellationToken cancellationToken)
    {
        var nodes = await ListAsync(cancellationToken);

        ComponentTypeNode? byGuid = nodes.FirstOrDefault(node =>
            string.Equals(node.Guid, type, StringComparison.OrdinalIgnoreCase));

        if (byGuid is not null)
        {
            return byGuid;
        }

        string normalized = type.Replace('/', '\\').Trim('\\', ' ');

        var byPath = nodes
            .Where(node => string.Equals(node.Path, normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (byPath.Count == 1)
        {
            return byPath[0];
        }

        var byName = nodes
            .Where(node => string.Equals(node.Name, normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (byName.Count == 1)
        {
            return byName[0];
        }

        if (byName.Count > 1)
        {
            throw new InvalidOperationException(
                $"The type name '{normalized}' occurs several times — specify the full path: "
                + string.Join(", ", byName.Select(node => node.Path)));
        }

        throw new InvalidOperationException(
            $"Component type '{type}' not found. vault_component_types gives the list.");
    }

    /// <summary>Creates a component type, nested in another one if needed.</summary>
    public async Task<ComponentTypeNode> CreateAsync(
        string name,
        string? parentType,
        CancellationToken cancellationToken)
    {
        ComponentTypeNode? parent = string.IsNullOrWhiteSpace(parentType)
            ? null
            : await ResolveAsync(parentType, cancellationToken);

        var existing = await ListAsync(cancellationToken);
        if (existing.Any(node =>
                string.Equals(node.ParentGuid ?? string.Empty, parent?.Guid ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                parent is null
                    ? $"Component type '{name}' already exists at the top level."
                    : $"'{parent.Path}' already has a type '{name}'.");
        }

        var tag = new ALU_Tag
        {
            GUID = Guid.NewGuid().ToString("D").ToUpperInvariant(),
            HRID = name,
            TagFamilyGUID = VaultTags.ComponentTypeFamilyGuid,
            ParentTagGUID = parent?.Guid ?? string.Empty,
        };

        await _gateway.AddTagsAsync([tag], cancellationToken);

        // In a dry run the type is not created and there is nowhere to re-read it.
        if (DryRun.IsActive)
        {
            return new ComponentTypeNode(tag.GUID, name, parent is null ? name : $"{parent.Path}\\{name}", parent?.Guid);
        }

        return (await ListAsync(cancellationToken)).First(node =>
            string.Equals(node.Guid, tag.GUID, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Renames a type and/or moves it under another parent.</summary>
    public async Task<ComponentTypeNode> UpdateAsync(
        string type,
        string? newName,
        string? newParentType,
        CancellationToken cancellationToken)
    {
        ComponentTypeNode node = await ResolveAsync(type, cancellationToken);
        var tags = await LoadTagsAsync(cancellationToken);

        ALU_Tag tag = tags.First(candidate =>
            string.Equals(candidate.GUID, node.Guid, StringComparison.OrdinalIgnoreCase));

        if (newName is not null)
        {
            tag.HRID = newName;
        }

        if (newParentType is not null)
        {
            if (newParentType.Length == 0)
            {
                tag.ParentTagGUID = string.Empty;
            }
            else
            {
                ComponentTypeNode parent = await ResolveAsync(newParentType, cancellationToken);

                // Moving a type into its own subtree would break the tree.
                if (IsDescendant(parent.Guid, node.Guid, tags))
                {
                    throw new InvalidOperationException(
                        $"'{parent.Path}' is inside '{node.Path}': such a move would break the type tree.");
                }

                tag.ParentTagGUID = parent.Guid;
            }
        }

        await _gateway.UpdateTagsAsync([tag], cancellationToken);

        if (DryRun.IsActive)
        {
            return new ComponentTypeNode(
                tag.GUID,
                tag.HRID ?? string.Empty,
                BuildPath(tag, tags.ToDictionary(candidate => candidate.GUID, StringComparer.OrdinalIgnoreCase)),
                string.IsNullOrEmpty(tag.ParentTagGUID) ? null : tag.ParentTagGUID);
        }

        return (await ListAsync(cancellationToken)).First(candidate =>
            string.Equals(candidate.Guid, node.Guid, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Deletes a component type together with the nested ones.</summary>
    public async Task<IReadOnlyList<string>> DeleteAsync(string type, CancellationToken cancellationToken)
    {
        ComponentTypeNode node = await ResolveAsync(type, cancellationToken);
        var tags = await LoadTagsAsync(cancellationToken);

        var doomed = tags
            .Where(tag => IsDescendant(tag.GUID, node.Guid, tags) || string.Equals(
                tag.GUID, node.Guid, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Nested types are deleted before the parent: otherwise references to nowhere remain.
        var ordered = doomed
            .OrderByDescending(tag => Depth(tag, tags))
            .Select(tag => tag.GUID)
            .ToList();

        await _gateway.DeleteTagsAsync(ordered, cancellationToken);

        return doomed.Select(tag => tag.HRID ?? tag.GUID).ToList();
    }

    /// <summary>Assigns a component type to items.</summary>
    public async Task AssignAsync(
        IReadOnlyCollection<string> itemGuids,
        string type,
        CancellationToken cancellationToken)
    {
        ComponentTypeNode node = await ResolveAsync(type, cancellationToken);

        var assignments = itemGuids.Select(itemGuid => new ALU_ItemTag
        {
            GUID = Guid.NewGuid().ToString("D").ToUpperInvariant(),
            ItemGUID = itemGuid,
            TagGUID = node.Guid,
        }).ToList();

        await _gateway.AssignItemTagsAsync(assignments, cancellationToken);
    }

    /// <summary>Types assigned to the given items: item GUID → type path.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetAssignedAsync(
        IReadOnlyCollection<string> itemGuids,
        CancellationToken cancellationToken)
    {
        if (itemGuids.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var itemTags = await VaultGateway.ReadInChunksAsync(
            itemGuids,
            chunk => _gateway.GetItemTagsAsync(
                VaultFilter.In("ItemGUID", chunk), limit: 100000, cancellationToken: cancellationToken));

        var nodes = (await ListAsync(cancellationToken))
            .ToDictionary(node => node.Guid, node => node.Path, StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (ALU_ItemTag itemTag in itemTags)
        {
            if (itemTag.ItemGUID is { Length: > 0 } item
                && nodes.TryGetValue(itemTag.TagGUID ?? string.Empty, out string? path))
            {
                result[item] = path;
            }
        }

        return result;
    }

    private Task<List<ALU_Tag>> LoadTagsAsync(CancellationToken cancellationToken) =>
        _gateway.GetTagsAsync(
            VaultFilter.Equal("TagFamilyGUID", VaultTags.ComponentTypeFamilyGuid), cancellationToken);

    private static bool IsDescendant(string candidate, string ancestor, IReadOnlyList<ALU_Tag> tags)
    {
        var byGuid = tags.ToDictionary(tag => tag.GUID, StringComparer.OrdinalIgnoreCase);
        string? current = candidate;
        int guard = 0;

        while (current is not null && guard++ < 64)
        {
            if (string.Equals(current, ancestor, StringComparison.OrdinalIgnoreCase) && guard > 1)
            {
                return true;
            }

            current = byGuid.TryGetValue(current, out ALU_Tag? tag) && !string.IsNullOrEmpty(tag.ParentTagGUID)
                ? tag.ParentTagGUID
                : null;
        }

        return false;
    }

    private static int Depth(ALU_Tag tag, IReadOnlyList<ALU_Tag> tags)
    {
        var byGuid = tags.ToDictionary(item => item.GUID, StringComparer.OrdinalIgnoreCase);
        int depth = 0;
        string? current = tag.ParentTagGUID;

        while (!string.IsNullOrEmpty(current) && depth < 64)
        {
            depth++;
            current = byGuid.TryGetValue(current, out ALU_Tag? parent) ? parent.ParentTagGUID : null;
        }

        return depth;
    }

    private static string BuildPath(ALU_Tag tag, IReadOnlyDictionary<string, ALU_Tag> byGuid)
    {
        var segments = new List<string>();
        ALU_Tag? current = tag;
        int guard = 0;

        while (current is not null && guard++ < 64)
        {
            segments.Add(current.HRID ?? string.Empty);

            current = !string.IsNullOrEmpty(current.ParentTagGUID)
                && byGuid.TryGetValue(current.ParentTagGUID, out ALU_Tag? parent)
                ? parent
                : null;
        }

        segments.Reverse();
        return string.Join('\\', segments);
    }
}
