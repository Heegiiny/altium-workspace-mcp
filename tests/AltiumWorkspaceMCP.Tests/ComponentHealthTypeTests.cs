using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>The "no component type" mismatch and its separation from the revision edit.</summary>
public sealed class ComponentHealthTypeTests
{
    private static ComponentRecord Record() => new()
    {
        ItemGuid = Guid.NewGuid().ToString(),
        Hrid = "CMP-000-0947",
        FolderPath = @"Components\X",
        FolderGuid = Guid.NewGuid().ToString(),
    };

    private static ComponentIssue TypeIssue(bool fixable = true) => new(
        ComponentHealthService.Kinds.MissingComponentType, "no type", fixable)
    {
        TypeGuid = fixable ? "24C51BB7-57FA-4BFA-94AC-977A056405C3" : null,
        TypePath = fixable ? @"Passive\Capacitors" : null,
    };

    [Fact]
    public void OnlyTypeIssueNeedsNoRevision()
    {
        var health = new ComponentHealth(Record(), [TypeIssue()]);

        Assert.True(health.IsFixable);
        Assert.False(health.NeedsRevisionFix);
        Assert.NotNull(health.TypeFix);
        Assert.Empty(health.FixChange.Parameters);
    }

    [Fact]
    public void FormatIssueStillNeedsRevisionAlongsideType()
    {
        var format = new ComponentIssue(ComponentHealthService.Kinds.MissingNumber, "number", Fixable: true)
        {
            Parameter = "Voltage",
        };

        var health = new ComponentHealth(Record(), [format, TypeIssue()]);

        Assert.True(health.NeedsRevisionFix);
        Assert.NotNull(health.TypeFix);
    }

    [Fact]
    public void UnfixableTypeIssueOffersNothing()
    {
        var health = new ComponentHealth(Record(), [TypeIssue(fixable: false)]);

        Assert.True(health.HasIssues);
        Assert.False(health.IsFixable);
        Assert.False(health.NeedsRevisionFix);
        Assert.Null(health.TypeFix);
    }

    [Fact]
    public void StaticInspectDoesNotReportTypes()
    {
        // The type is checked only by InspectAsync (it needs tags and templates); Inspect is still purely about the format.
        Assert.DoesNotContain(
            ComponentHealthService.Inspect(Record(), [], []),
            issue => issue.Kind == ComponentHealthService.Kinds.MissingComponentType);
    }

    /// <summary>"A reference to a model in the trash": fix does not repair it, it hints at the command.</summary>
    [Fact]
    public void DeletedModelIssueIsNotFixableAndNamesRestoreCommand()
    {
        ComponentIssue issue = ComponentHealthService.DescribeDeletedModel("footprint", "PCC-0009", "MODEL-GUID");

        Assert.Equal(ComponentHealthService.Kinds.ModelInTrash, issue.Kind);
        Assert.False(issue.Fixable);
        Assert.Contains("PCC-0009", issue.Detail);
        Assert.Contains("vault_restore_items", issue.Detail);
        Assert.Contains("MODEL-GUID", issue.Detail);
    }

    [Fact]
    public void DeletedModelIssueDoesNotCountAsFixable()
    {
        var health = new ComponentHealth(
            Record(), [ComponentHealthService.DescribeDeletedModel("symbol", "SYM-000-0076", "MODEL-GUID")]);

        Assert.True(health.HasIssues);
        Assert.False(health.IsFixable);
        Assert.False(health.NeedsRevisionFix);
        Assert.Null(health.TypeFix);
    }
}
