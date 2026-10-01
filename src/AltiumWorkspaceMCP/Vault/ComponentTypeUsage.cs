namespace AltiumWorkspaceMCP.Vault;

/// <summary>A type from the subtree being deleted: how many parts it has and which templates.</summary>
public sealed record TypeUsageLine(string Path, int Details, IReadOnlyList<string> Templates);

/// <summary>
/// What keeps a component type from deletion: the parts with this type and the templates in which it is
/// written in <c>.cmpt</c>. Pure logic: the data comes from the search service and <c>TemplateService</c>.
/// </summary>
public static class ComponentTypeUsage
{
    /// <summary>The type and all nested ones.</summary>
    public static IReadOnlyList<ComponentTypeNode> Subtree(IReadOnlyList<ComponentTypeNode> nodes, ComponentTypeNode root)
    {
        string key = ComponentTypeTree.Key(root.Path);

        return nodes
            .Where(node => string.Equals(node.Guid, root.Guid, StringComparison.OrdinalIgnoreCase)
                || ComponentTypeTree.Key(node.Path).StartsWith(key + "\\", StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// Rows only for types that have parts or templates.
    /// </summary>
    /// <param name="detailCounts">Part count by type: the key is <see cref="ComponentTypeTree.Key"/> of the path.</param>
    /// <param name="templatesByType">Templates by the type GUID from <c>.cmpt</c>.</param>
    public static IReadOnlyList<TypeUsageLine> Find(
        IReadOnlyList<ComponentTypeNode> subtree,
        IReadOnlyDictionary<string, int> detailCounts,
        IReadOnlyDictionary<string, IReadOnlyList<string>> templatesByType)
    {
        var lines = new List<TypeUsageLine>();

        foreach (ComponentTypeNode node in subtree.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
        {
            int details = detailCounts.GetValueOrDefault(ComponentTypeTree.Key(node.Path));
            IReadOnlyList<string> templates = templatesByType.GetValueOrDefault(node.Guid) ?? [];

            if (details > 0 || templates.Count > 0)
            {
                lines.Add(new TypeUsageLine(node.Path, details, templates));
            }
        }

        return lines;
    }

    /// <summary>Refusal text: type → parts, templates; the first 10 rows.</summary>
    public static string DescribeRefusal(string typePath, IReadOnlyList<TypeUsageLine> lines)
    {
        const int Shown = 10;

        string list = string.Join("; ", lines.Take(Shown).Select(line =>
            $"{line.Path} → parts: {line.Details}, templates: {line.Templates.Count}"
            + (line.Templates.Count > 0 ? $" ({string.Join(", ", line.Templates.Take(3))}{(line.Templates.Count > 3 ? ", …" : string.Empty)})" : string.Empty)))
            + (lines.Count > Shown ? $"; … and {lines.Count - Shown} more types" : string.Empty);

        return $"Type '{typePath}' cannot be deleted: it or its nested types have parts or templates — {list}. "
            + "Deleting a type is irreversible (there is no trash). First reassign the parts "
            + "(vault_component_types assign) and the templates (vault_template set_type), then delete. "
            + "The search index by which parts are counted is updated with a delay: just reassigned "
            + "parts may still be counted under the old type for a minute or two.";
    }
}
