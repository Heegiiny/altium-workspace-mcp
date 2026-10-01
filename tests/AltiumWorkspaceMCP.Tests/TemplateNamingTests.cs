using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>Template name in a folder and protection of templates from the general table edit.</summary>
public sealed class TemplateNamingTests
{
    private const string Folder = @"Managed Content\Templates\Component Templates";

    private static ComponentTemplate Template(string hrid, string folder, string? comment) =>
        new(hrid, "guid-" + hrid, "rev-" + hrid, "1", folder, null, null, new Dictionary<string, string>(), comment);

    private static ComponentRecord Record(string hrid, string? contentType) =>
        new() { ItemGuid = "g-" + hrid, Hrid = hrid, FolderPath = Folder, FolderGuid = "f", ContentType = contentType };

    [Fact]
    public void TwinInSameFolderIsFound()
    {
        var templates = new[] { Template("CMPT-1", Folder, "BJT"), Template("CMPT-2", Folder, "MOSFET") };

        Assert.Equal("CMPT-1", TemplateService.FindNameTwin(templates, Folder, "BJT")?.Hrid);
    }

    [Fact]
    public void CaseAndEdgeSpacesDoNotMakeNamesDifferent()
    {
        var templates = new[] { Template("CMPT-1", Folder, " bjt ") };

        Assert.NotNull(TemplateService.FindNameTwin(templates, Folder.ToUpperInvariant(), "BJT  "));
    }

    [Fact]
    public void RenamedTemplateIsNotItsOwnTwin()
    {
        var templates = new[] { Template("CMPT-1", Folder, "BJT") };

        Assert.Null(TemplateService.FindNameTwin(templates, Folder, "BJT", excludeItemGuid: "guid-CMPT-1"));
    }

    [Fact]
    public void OtherTemplateWithSameNameIsTwinEvenWhenOneIsExcluded()
    {
        var templates = new[] { Template("CMPT-1", Folder, "BJT"), Template("CMPT-2", Folder, "BJT") };

        Assert.Equal("CMPT-2", TemplateService.FindNameTwin(templates, Folder, "BJT", excludeItemGuid: "guid-CMPT-1")?.Hrid);
    }

    [Fact]
    public void SameNameInAnotherFolderIsNotTwin()
    {
        var templates = new[] { Template("CMPT-1", @"Components\Test", "BJT") };

        Assert.Null(TemplateService.FindNameTwin(templates, Folder, "BJT"));
    }

    [Fact]
    public void TemplateWithoutNameIsNeverTwin()
    {
        var templates = new[] { Template("CMPT-1", Folder, null) };

        Assert.Null(TemplateService.FindNameTwin(templates, Folder, "BJT"));
    }

    [Fact]
    public void EnsureNameIsFreeNamesTwinAndAdvises()
    {
        var templates = new[] { Template("CMPT-1", Folder, "BJT") };

        var error = Assert.Throws<InvalidOperationException>(() => TemplateService.EnsureNameIsFree(templates, Folder, "BJT"));

        Assert.Contains("CMPT-1", error.Message, StringComparison.Ordinal);
        Assert.Contains("another name", error.Message, StringComparison.Ordinal);
        TemplateService.EnsureNameIsFree(templates, Folder, "BJT", "guid-CMPT-1");
    }

    [Fact]
    public void TemplatesAreRefusedByNameWithAdvice()
    {
        var records = new[] { Record("CMP-1", "altium-component"), Record("CMPT-7", TemplateService.ContentTypeName), Record("CMPT-8", "ALTIUM-COMPONENT-TEMPLATE") };

        var error = Assert.Throws<InvalidOperationException>(() => TemplateService.EnsureNoTemplates(records));

        Assert.Contains("CMPT-7", error.Message, StringComparison.Ordinal);
        Assert.Contains("CMPT-8", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("CMP-1", error.Message, StringComparison.Ordinal);
        Assert.Contains("vault_template set", error.Message, StringComparison.Ordinal);
        Assert.Contains(".cmpt", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryComponentsPassTheTemplateGuard() =>
        TemplateService.EnsureNoTemplates([Record("CMP-1", "altium-component"), Record("CMP-2", null)]);

    /// <summary>
    /// vault_set_links and vault_repair_links check only the components being written: a template as a link target
    /// (role: template) is not checked, and the usual relinking of parts onto a template works.
    /// </summary>
    [Fact]
    public void TemplateAsLinkTargetIsNotWrittenSoGuardPassesTheParts()
    {
        var writtenParts = new[] { Record("CMP-1", "altium-component"), Record("CMP-2", "altium-component") };

        TemplateService.EnsureNoTemplates(writtenParts);
    }

    [Fact]
    public void GuardRefusesTemplateAmongPartsForLinkTools()
    {
        var mixed = new[] { Record("CMP-1", "altium-component"), Record("CMPT-0007", TemplateService.ContentTypeName) };

        var error = Assert.Throws<InvalidOperationException>(() => TemplateService.EnsureNoTemplates(mixed));

        Assert.Contains("CMPT-0007", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("CMP-1,", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Copying a template refuses with a hint at create basedOn.</summary>
    [Fact]
    public void CopySourceThatIsTemplateIsRefusedWithAdvice()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => TemplateService.EnsureNotCopySource(Record("CMPT-0007", TemplateService.ContentTypeName)));

        Assert.Contains("CMPT-0007", error.Message, StringComparison.Ordinal);
        Assert.Contains("vault_template create basedOn=CMPT-0007", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryComponentPassesTheCopyGuard() =>
        TemplateService.EnsureNotCopySource(Record("CMP-1", "altium-component"));
}
