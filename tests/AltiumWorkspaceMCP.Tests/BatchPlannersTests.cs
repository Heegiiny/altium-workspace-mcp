using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>Creating folders and moving in a batch: parent order, skipping existing ones, group overlap.</summary>
public sealed class BatchPlannersTests
{
    private static FolderNode Folder(string path, string? parentGuid = null, string type = "T-LIB") =>
        new(Guid.NewGuid().ToString(), path[(path.LastIndexOf('\\') + 1)..], path, null, parentGuid, type);

    private static readonly FolderNode Root = Folder("Components");

    [Fact]
    public void ParentsAreCreatedBeforeChildren()
    {
        // The children are listed before the parents — the write order is still from the root to the leaves.
        var plan = FolderCreationPlanner.Plan(
            [Root],
            [@"Components\A\B\C", @"Components\A", @"Components\X\Y"]);

        Assert.Empty(plan.Conflicts);
        Assert.Equal(
            [@"Components\A", @"Components\X", @"Components\A\B", @"Components\X\Y", @"Components\A\B\C"],
            plan.ToCreate.Select(folder => folder.Path));

        var byPath = plan.ToCreate.ToDictionary(folder => folder.Path);
        Assert.Equal(Root.Guid, byPath[@"Components\A"].ParentGuid);
        Assert.Equal(byPath[@"Components\A"].Guid, byPath[@"Components\A\B"].ParentGuid);
        Assert.Equal(byPath[@"Components\A\B"].Guid, byPath[@"Components\A\B\C"].ParentGuid);
        Assert.All(plan.ToCreate, folder => Assert.Equal("T-LIB", folder.InheritedTypeGuid));
    }

    [Fact]
    public void ExistingFoldersAreSkippedAndDuplicatesCollapse()
    {
        var plan = FolderCreationPlanner.Plan(
            [Root, Folder(@"Components\A")],
            [@"components/a", @"Components\A\New", @"Components\A\New", @"COMPONENTS\a\new"]);

        Assert.Equal([@"Components\A"], plan.Skipped);
        Assert.Equal([@"Components\A\New"], plan.ToCreate.Select(folder => folder.Path));
    }

    [Fact]
    public void NestedChainOfTwentyIsOrderedByDepth()
    {
        string path = "Components";
        var paths = new List<string>();

        for (int level = 1; level <= 20; level++)
        {
            path += $"\\L{level}";
            paths.Add(path);
        }

        paths.Reverse();

        var plan = FolderCreationPlanner.Plan([Root], paths);

        Assert.Equal(20, plan.ToCreate.Count);
        Assert.Equal(Enumerable.Range(1, 20), plan.ToCreate.Select(folder => folder.Depth));
    }

    [Fact]
    public void ConflictsAreListedTogetherAndBlockOnlyTheirPaths()
    {
        var plan = FolderCreationPlanner.Plan(
            [Root],
            [@"Nowhere\A", @"Components\Datasheets\X", "  ", @"Components\Good"]);

        Assert.Equal(3, plan.Conflicts.Count);
        Assert.Equal([@"Components\Good"], plan.ToCreate.Select(folder => folder.Path));
    }

    private static MoveCandidate Item(string hrid, string folderGuid) =>
        new(hrid + "-guid", hrid, folderGuid, "Somewhere");

    [Fact]
    public void ItemInTwoGroupsIsAnOverlap()
    {
        var a = Folder(@"Components\A");
        var b = Folder(@"Components\B");

        var plan = MoveBatchPlanner.Plan(
        [
            new MoveGroupInput(a, [Item("CMP-1", "x"), Item("CMP-2", "x")]),
            new MoveGroupInput(b, [Item("CMP-2", "x"), Item("CMP-3", "x")]),
        ]);

        Assert.Single(plan.Overlaps);
        Assert.Contains("CMP-2", plan.Overlaps[0]);
        Assert.Equal(3, plan.MovingCount);
    }

    [Fact]
    public void ItemsAlreadyInTargetAreSkipped()
    {
        var a = Folder(@"Components\A");
        var b = Folder(@"Components\B");

        var plan = MoveBatchPlanner.Plan(
        [
            new MoveGroupInput(a, [Item("CMP-1", a.Guid), Item("CMP-2", "x")]),
            new MoveGroupInput(b, [Item("CMP-3", b.Guid)]),
        ]);

        Assert.Empty(plan.Overlaps);
        Assert.Equal(1, plan.MovingCount);
        Assert.Equal(["CMP-1"], plan.Groups[0].AlreadyThere);
        Assert.Equal(["CMP-3"], plan.Groups[1].AlreadyThere);
    }

    [Fact]
    public void RepeatedItemInsideOneGroupCountsOnce()
    {
        var a = Folder(@"Components\A");

        var plan = MoveBatchPlanner.Plan([new MoveGroupInput(a, [Item("CMP-1", "x"), Item("CMP-1", "x")])]);

        Assert.Empty(plan.Overlaps);
        Assert.Equal(1, plan.MovingCount);
    }
}
