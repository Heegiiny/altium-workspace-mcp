namespace AltiumWorkspaceMCP.Vault;

/// <summary>
/// Checking the <c>ModelLinks</c> references of templates to the default symbol and footprint:
/// read-only. Collects the GUIDs of all references (of one template or of all at once) and resolves them
/// in batches — first live items by one read, the remaining ones are looked up in the trash by one or two more
/// reads of <see cref="TrashService.FindItemsAsync"/>, not by a request per template.
/// </summary>
public sealed class TemplateLinkCheckService
{
    private readonly TemplateService _templates;
    private readonly ComponentService _components;
    private readonly TrashService _trash;

    public TemplateLinkCheckService(TemplateService templates, ComponentService components, TrashService trash)
    {
        _templates = templates;
        _components = components;
        _trash = trash;
    }

    /// <param name="template">Identifier or GUID of one template; <see langword="null"/> — all templates.</param>
    public async Task<TemplateLinkCheckSummary> CheckAsync(string? template, CancellationToken cancellationToken)
    {
        int templatesChecked;
        IReadOnlyList<TemplateModelReference> references;

        if (template is null)
        {
            var all = await _templates.ListAsync(cancellationToken);
            templatesChecked = all.Count;
            references = await _templates.ListModelReferencesAsync(cancellationToken);
        }
        else
        {
            ComponentTemplate resolved = await _templates.ResolveAsync(template, cancellationToken);
            templatesChecked = 1;
            references = await _templates.ListModelReferencesAsync(resolved, cancellationToken);
        }

        var modelGuids = references
            .Select(reference => reference.ModelItemGuid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var live = modelGuids.Count == 0
            ? []
            : await _components.ReadByIdsAsync(modelGuids, cancellationToken);
        var liveByGuid = live.ToDictionary(record => record.ItemGuid, StringComparer.OrdinalIgnoreCase);

        var stillMissing = modelGuids.Where(guid => !liveByGuid.ContainsKey(guid)).ToList();
        var trash = stillMissing.Count == 0
            ? new Dictionary<string, TrashItemLocation>(StringComparer.OrdinalIgnoreCase)
            : await _trash.FindItemsAsync(stillMissing, cancellationToken);

        var checks = TemplateLinkChecker.Build(references, liveByGuid, trash);

        return TemplateLinkChecker.Summarize(templatesChecked, checks);
    }
}
