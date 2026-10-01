using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>Status of the ModelLinks references of templates to the default symbol and footprint.</summary>
public sealed class TemplateLinkCheckTests
{
    private static ComponentRecord Live(string itemGuid, string hrid) => new()
    {
        ItemGuid = itemGuid,
        Hrid = hrid,
        FolderPath = "Components\\Models",
        FolderGuid = "folder-1",
    };

    private static TrashItemLocation Trashed(string guid, string hrid, string path, DateTimeOffset deletedAt) =>
        new(guid, hrid, path, deletedAt);

    private static IReadOnlyList<TemplateModelReference> References() =>
        TemplateReferenceIndex.Build(
        [
            ("CMPT-0007", "tpl-7", new TemplateSettings(null, null, false, "sym-live", "fp-trash")),
            ("CMPT-0000", "tpl-0", new TemplateSettings(null, null, false, "sym-missing", null)),
        ]);

    [Fact]
    public void LiveModelIsOk()
    {
        var live = new Dictionary<string, ComponentRecord>(StringComparer.OrdinalIgnoreCase)
        {
            ["sym-live"] = Live("sym-live", "SYM-0006"),
        };
        var trash = new Dictionary<string, TrashItemLocation>(StringComparer.OrdinalIgnoreCase);

        var checks = TemplateLinkChecker.Build(References(), live, trash);
        ModelLinkCheck symbolCheck = Assert.Single(checks, check => check.TemplateHrid == "CMPT-0007" && check.LinkName == LinkRole.Symbol);

        Assert.Equal(ModelLinkStatus.Ok, symbolCheck.Status);
        Assert.Equal("SYM-0006", symbolCheck.Hrid);
        Assert.Null(symbolCheck.DeletedAt);
    }

    [Fact]
    public void ModelInTrashCarriesRestoredPathAndDate()
    {
        var deletedAt = new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);
        var live = new Dictionary<string, ComponentRecord>(StringComparer.OrdinalIgnoreCase);
        var trash = new Dictionary<string, TrashItemLocation>(StringComparer.OrdinalIgnoreCase)
        {
            ["fp-trash"] = Trashed("fp-trash", "PCC-0084", "Models\\Footprints", deletedAt),
        };

        var checks = TemplateLinkChecker.Build(References(), live, trash);
        ModelLinkCheck footprintCheck = Assert.Single(checks, check => check.TemplateHrid == "CMPT-0007" && check.LinkName == LinkRole.Footprint);

        Assert.Equal(ModelLinkStatus.InTrash, footprintCheck.Status);
        Assert.Equal("PCC-0084", footprintCheck.Hrid);
        Assert.Equal("Models\\Footprints", footprintCheck.RestoredPath);
        Assert.Equal(deletedAt, footprintCheck.DeletedAt);
    }

    [Fact]
    public void ModelNeitherLiveNorInTrashIsMissing()
    {
        var live = new Dictionary<string, ComponentRecord>(StringComparer.OrdinalIgnoreCase);
        var trash = new Dictionary<string, TrashItemLocation>(StringComparer.OrdinalIgnoreCase);

        var checks = TemplateLinkChecker.Build(References(), live, trash);
        ModelLinkCheck missing = Assert.Single(checks, check => check.TemplateHrid == "CMPT-0000");

        Assert.Equal(ModelLinkStatus.Missing, missing.Status);
        Assert.Null(missing.Hrid);
        Assert.Null(missing.RestoredPath);
        Assert.Null(missing.DeletedAt);
    }

    [Fact]
    public void SummarizeCountsOnlyProblemsAndDistinctTemplates()
    {
        var live = new Dictionary<string, ComponentRecord>(StringComparer.OrdinalIgnoreCase)
        {
            ["sym-live"] = Live("sym-live", "SYM-0006"),
        };
        var trash = new Dictionary<string, TrashItemLocation>(StringComparer.OrdinalIgnoreCase)
        {
            ["fp-trash"] = Trashed("fp-trash", "PCC-0084", "Models\\Footprints", DateTimeOffset.UtcNow),
        };

        var checks = TemplateLinkChecker.Build(References(), live, trash);
        TemplateLinkCheckSummary summary = TemplateLinkChecker.Summarize(templatesChecked: 2, checks);

        Assert.Equal(2, summary.TemplatesChecked);
        Assert.Equal(3, summary.LinksChecked);
        Assert.Equal(2, summary.TemplatesWithProblems);
        Assert.Equal(2, summary.Problems.Count);
        Assert.DoesNotContain(summary.Problems, problem => problem.Status == ModelLinkStatus.Ok);
    }

    [Fact]
    public void SummarizeWithNoProblemsIsEmpty()
    {
        var live = new Dictionary<string, ComponentRecord>(StringComparer.OrdinalIgnoreCase)
        {
            ["sym-live"] = Live("sym-live", "SYM-0006"),
        };

        var references = TemplateReferenceIndex.Build(
        [
            ("CMPT-0007", "tpl-7", new TemplateSettings(null, null, false, "sym-live", null)),
        ]);

        var checks = TemplateLinkChecker.Build(references, live, new Dictionary<string, TrashItemLocation>(StringComparer.OrdinalIgnoreCase));
        TemplateLinkCheckSummary summary = TemplateLinkChecker.Summarize(1, checks);

        Assert.Equal(0, summary.TemplatesWithProblems);
        Assert.Empty(summary.Problems);
    }

    [Fact]
    public void LabelsFormatTrashAndMissingText()
    {
        var deletedAt = new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal("in trash: SYM-0006 (deleted 2026-09-22)", ModelLinkLabels.InTrash("SYM-0006", deletedAt));
        Assert.Equal("not in vault: guid-1", ModelLinkLabels.Missing("guid-1"));
        Assert.Equal(
            "in trash: SYM-0006 (deleted 2026-09-22)",
            ModelLinkLabels.Format(ModelLinkStatus.InTrash, "guid-1", "SYM-0006", deletedAt));
        Assert.Equal("not in vault: guid-1", ModelLinkLabels.Format(ModelLinkStatus.Missing, "guid-1", null, null));
        Assert.Equal("SYM-0006", ModelLinkLabels.Format(ModelLinkStatus.Ok, "guid-1", "SYM-0006", null));
    }

    [Fact]
    public void ReferencesCarryModelLinkNameForHints()
    {
        var references = References();

        TemplateModelReference symbol = Assert.Single(references, reference => reference.TemplateHrid == "CMPT-0007" && reference.Role == "symbol");
        TemplateModelReference footprint = Assert.Single(references, reference => reference.TemplateHrid == "CMPT-0007" && reference.Role == "footprint");

        Assert.Equal(LinkRole.Symbol, symbol.LinkName);
        Assert.Equal(LinkRole.Footprint, footprint.LinkName);
    }
}
