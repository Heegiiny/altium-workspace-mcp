namespace AltiumWorkspaceMCP.Vault;

/// <summary>State of a template's reference to the default symbol or footprint.</summary>
public enum ModelLinkStatus
{
    /// <summary>The item is alive.</summary>
    Ok,

    /// <summary>The item is in the vault trash.</summary>
    InTrash,

    /// <summary>The item is neither among the live ones nor in the trash.</summary>
    Missing,
}

/// <summary>
/// Result of checking one <c>ModelLinks</c> reference of a template: the item is alive, in the trash or gone.
/// <see cref="Hrid"/>/<see cref="RestoredPath"/>/<see cref="DeletedAt"/> are filled only for
/// <see cref="ModelLinkStatus.Ok"/> and <see cref="ModelLinkStatus.InTrash"/>.
/// </summary>
public sealed record ModelLinkCheck(
    string TemplateHrid,
    string TemplateItemGuid,
    string LinkName,
    string Role,
    string ModelItemGuid,
    ModelLinkStatus Status,
    string? Hrid = null,
    string? RestoredPath = null,
    DateTimeOffset? DeletedAt = null);

/// <summary>Check summary: how many templates and references were checked and what problems were found.</summary>
public sealed record TemplateLinkCheckSummary(
    int TemplatesChecked,
    int LinksChecked,
    int TemplatesWithProblems,
    IReadOnlyList<ModelLinkCheck> Problems);

/// <summary>
/// Classification of template references to models — pure logic without server calls:
/// the result is prepared by the batch reads <see cref="ComponentService.ReadByIdsAsync"/> (live ones) and
/// <see cref="TrashService.FindItemsAsync"/> (trash), this part only matches them with the
/// references collected by <see cref="TemplateReferenceIndex"/>.
/// </summary>
public static class TemplateLinkChecker
{
    public static IReadOnlyList<ModelLinkCheck> Build(
        IReadOnlyList<TemplateModelReference> references,
        IReadOnlyDictionary<string, ComponentRecord> live,
        IReadOnlyDictionary<string, TrashItemLocation> trash)
    {
        var result = new List<ModelLinkCheck>(references.Count);

        foreach (TemplateModelReference reference in references)
        {
            if (live.TryGetValue(reference.ModelItemGuid, out ComponentRecord? record))
            {
                result.Add(new ModelLinkCheck(
                    reference.TemplateHrid, reference.TemplateItemGuid, reference.LinkName, reference.Role,
                    reference.ModelItemGuid, ModelLinkStatus.Ok, Hrid: record.Hrid));
            }
            else if (trash.TryGetValue(reference.ModelItemGuid, out TrashItemLocation? location))
            {
                result.Add(new ModelLinkCheck(
                    reference.TemplateHrid, reference.TemplateItemGuid, reference.LinkName, reference.Role,
                    reference.ModelItemGuid, ModelLinkStatus.InTrash,
                    Hrid: location.Hrid, RestoredPath: location.RestoredPath, DeletedAt: location.DeletedAt));
            }
            else
            {
                result.Add(new ModelLinkCheck(
                    reference.TemplateHrid, reference.TemplateItemGuid, reference.LinkName, reference.Role,
                    reference.ModelItemGuid, ModelLinkStatus.Missing));
            }
        }

        return result;
    }

    /// <summary>Summary of the <see cref="Build"/> results: how many templates were checked and how many have problems.</summary>
    public static TemplateLinkCheckSummary Summarize(int templatesChecked, IReadOnlyList<ModelLinkCheck> checks)
    {
        var problems = checks.Where(check => check.Status != ModelLinkStatus.Ok).ToList();

        int templatesWithProblems = problems
            .Select(problem => problem.TemplateItemGuid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        return new TemplateLinkCheckSummary(templatesChecked, checks.Count, templatesWithProblems, problems);
    }
}

/// <summary>A short text instead of the raw GUID of a model that <c>vault_template show</c>/<c>vault_templates</c> did not show.</summary>
public static class ModelLinkLabels
{
    public static string InTrash(string hrid, DateTimeOffset deletedAt) =>
        $"in trash: {hrid} (deleted {deletedAt:yyyy-MM-dd})";

    public static string Missing(string itemGuid) => $"not in vault: {itemGuid}";

    /// <summary>A label by the check status — the same one <c>vault_template check</c> uses.</summary>
    public static string Format(ModelLinkStatus status, string itemGuid, string? hrid, DateTimeOffset? deletedAt) =>
        status switch
        {
            ModelLinkStatus.Ok => hrid ?? itemGuid,
            ModelLinkStatus.InTrash => InTrash(hrid ?? itemGuid, deletedAt ?? default),
            _ => Missing(itemGuid),
        };
}
