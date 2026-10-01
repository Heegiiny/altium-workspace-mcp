namespace AltiumWorkspaceMCP.Vault;

/// <summary>An item that must not be deleted: who references it.</summary>
/// <param name="UsedBy">Live parts whose active revision references the item.</param>
/// <param name="Templates">Templates in which the item is the default symbol or footprint.</param>
public sealed record BlockedModel(
    string ItemGuid,
    string Hrid,
    IReadOnlyList<LiveUsage> UsedBy,
    IReadOnlyList<string>? Templates = null);

/// <summary>
/// A template reference to an item: the default symbol or footprint.
/// <paramref name="LinkName"/> — the code <see cref="LinkRole.Symbol"/>/<see cref="LinkRole.Footprint"/>
/// (the name of the <c>ModelLinks</c> object in .cmpt), <paramref name="Role"/> — the same as text for display.
/// </summary>
public sealed record TemplateModelReference(
    string TemplateHrid, string TemplateItemGuid, string ModelItemGuid, string LinkName, string Role);

/// <summary>
/// Reverse pointer "item → templates in which it is the default model"
/// (<c>ModelLinks</c> in <c>.cmpt</c>). Pure logic.
/// </summary>
public static class TemplateReferenceIndex
{
    public static IReadOnlyList<TemplateModelReference> Build(
        IEnumerable<(string Hrid, string ItemGuid, TemplateSettings Settings)> templates)
    {
        var references = new List<TemplateModelReference>();

        foreach (var (hrid, itemGuid, settings) in templates)
        {
            if (!string.IsNullOrEmpty(settings.DefaultSymbolItemGuid))
            {
                references.Add(new TemplateModelReference(
                    hrid, itemGuid, settings.DefaultSymbolItemGuid, LinkRole.Symbol, LinkRole.Describe(LinkRole.Symbol)));
            }

            if (!string.IsNullOrEmpty(settings.DefaultFootprintItemGuid))
            {
                references.Add(new TemplateModelReference(
                    hrid, itemGuid, settings.DefaultFootprintItemGuid, LinkRole.Footprint, LinkRole.Describe(LinkRole.Footprint)));
            }
        }

        return references;
    }

    /// <summary>Templates referencing the item; templates from <paramref name="exclude"/> (deleted together with it) do not count.</summary>
    public static IReadOnlyList<string> Find(
        IReadOnlyList<TemplateModelReference> references, string itemGuid, IReadOnlySet<string> exclude) =>
        references
            .Where(reference => string.Equals(reference.ModelItemGuid, itemGuid, StringComparison.OrdinalIgnoreCase)
                && !exclude.Contains(reference.TemplateItemGuid))
            .Select(reference => $"{reference.TemplateHrid} ({reference.Role} as default)")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(text => text, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// HRIDs of templates in which the item is the default symbol or footprint —
    /// without the role and without exclusions, for the trash row.
    /// </summary>
    public static IReadOnlyList<string> FindTemplateHrids(IReadOnlyList<TemplateModelReference> references, string itemGuid) =>
        references
            .Where(reference => string.Equals(reference.ModelItemGuid, itemGuid, StringComparison.OrdinalIgnoreCase))
            .Select(reference => reference.TemplateHrid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(hrid => hrid, StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>
/// One check "this item must not be deleted" for all deletion paths: references
/// of the active revisions of live parts and templates. Always checked, not only with force.
/// </summary>
public sealed class DeletionGuard
{
    private readonly UsageService _usage;
    private readonly TemplateService _templates;

    public DeletionGuard(UsageService usage, TemplateService templates)
    {
        _usage = usage;
        _templates = templates;
    }

    /// <summary>
    /// Items from <paramref name="items"/> that live parts or templates outside
    /// the set itself reference. Could not check — an exception, not "free".
    /// </summary>
    public async Task<IReadOnlyList<BlockedModel>> FindBlockingAsync(
        IReadOnlyList<(string Guid, string Hrid)> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var exclude = items.Select(item => item.Guid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var references = await _templates.ListModelReferencesAsync(cancellationToken);
        var blocked = new List<BlockedModel>();

        foreach ((string guid, string hrid) in items)
        {
            var usage = await _usage.FindExternalUsageAsync(guid, exclude, cancellationToken);
            var templates = TemplateReferenceIndex.Find(references, guid, exclude);

            if (usage.Count > 0 || templates.Count > 0)
            {
                blocked.Add(new BlockedModel(guid, hrid, usage, templates));
            }
        }

        return blocked;
    }

    /// <summary>List for the refusal text: the model, how many parts and which, which templates.</summary>
    public static string DescribeDetails(IReadOnlyList<BlockedModel> blocked) =>
        string.Join("; ", blocked.Select(model =>
        {
            var parts = new List<string>();

            if (model.UsedBy.Count > 0)
            {
                parts.Add($"{model.UsedBy.Count} parts ({string.Join(", ", model.UsedBy.Take(5)
                    .Select(usage => $"{usage.Hrid} [{usage.FolderPath}]"))}"
                    + (model.UsedBy.Count > 5 ? ", …" : string.Empty) + ")");
            }

            if (model.Templates is { Count: > 0 })
            {
                parts.Add($"templates: {string.Join(", ", model.Templates)}");
            }

            return $"{model.Hrid} → {string.Join("; ", parts)}";
        }));
}
