using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>The compact folder tree for vault_folders.</summary>
public sealed class FolderTreeViewTests
{
    private static readonly FolderNode[] Tree = Build(
        ("Components", null),
        (@"Components\Passive Components", "Passives"),
        (@"Components\Passive Components\Resistors", ""),
        (@"Components\Passive Components\Resistors\SMD", null),
        (@"Components\Passive Components\Resistors\SMD\0603", null),
        (@"Components\Passive Components\Resistors\Datasheets", null),
        (@"Components\Passive Components\Capacitors", null),
        (@"Components\Integrated Circuits", null),
        (@"Components\Integrated Circuits\Logic", null),
        ("Symbols", null),
        (@"Symbols\Resistors", null));

    private static FolderNode[] Build(params (string Path, string? Description)[] items) =>
        items.Select((item, index) => new FolderNode(
            Guid: $"00000000-0000-0000-0000-{index:D12}",
            Name: item.Path[(item.Path.LastIndexOf('\\') + 1)..],
            Path: item.Path,
            Description: item.Description,
            ParentGuid: null,
            FolderTypeGuid: null)).ToArray();

    private static FolderNode Folder(string path) => Tree.First(folder => folder.Path == path);

    [Fact]
    public void DefaultWithoutRootShowsTwoLevelsAndCountsHidden()
    {
        var lines = FolderTreeView.Levels(Tree, null, depth: 2, includeSystem: false, includeGuids: false);

        Assert.Equal(
            [
                "Components",
                @"Components\Integrated Circuits  [+1]",
                @"Components\Passive Components  [+4]",
                "Symbols",
                @"Symbols\Resistors",
            ],
            lines.Select(line => line.Text));

        Assert.Equal(0, lines.First(line => line.Folder.Path == "Components").Hidden);
    }

    [Fact]
    public void HiddenCountSkipsSystemFoldersUnlessAsked()
    {
        var withoutSystem = FolderTreeView.Levels(Tree, null, depth: 3, includeSystem: false, includeGuids: false);
        var withSystem = FolderTreeView.Levels(Tree, null, depth: 3, includeSystem: true, includeGuids: false);

        Assert.DoesNotContain(
            FolderTreeView.Levels(Tree, null, depth: 4, includeSystem: false, includeGuids: false),
            line => line.Folder.Name == "Datasheets");
        Assert.Contains(
            FolderTreeView.Levels(Tree, null, depth: 4, includeSystem: true, includeGuids: false),
            line => line.Folder.Name == "Datasheets");

        Assert.Equal(2, withoutSystem.First(line => line.Folder.Path == @"Components\Passive Components\Resistors").Hidden);
        Assert.Equal(3, withSystem.First(line => line.Folder.Path == @"Components\Passive Components\Resistors").Hidden);
    }

    [Fact]
    public void RootCountsAsFirstLevelAndDepthIsRelative()
    {
        var root = Folder(@"Components\Passive Components");

        var lines = FolderTreeView.Levels(Tree, root, depth: 2, includeSystem: false, includeGuids: false);

        Assert.Equal(
            [
                @"Components\Passive Components — Passives",
                @"Components\Passive Components\Capacitors",
                @"Components\Passive Components\Resistors  [+2]",
            ],
            lines.Select(line => line.Text));

        var deep = FolderTreeView.Levels(Tree, root, depth: 5, includeSystem: false, includeGuids: false);
        Assert.Contains(deep, line => line.Folder.Path.EndsWith(@"SMD\0603", StringComparison.Ordinal));
        Assert.All(deep, line => Assert.Equal(0, line.Hidden));
    }

    [Fact]
    public void GuidsAreAddedOnlyOnRequest()
    {
        var root = Folder("Symbols");

        var plain = FolderTreeView.Levels(Tree, root, 2, false, includeGuids: false);
        var withGuids = FolderTreeView.Levels(Tree, root, 2, false, includeGuids: true);

        Assert.DoesNotContain("|", plain[0].Text);
        Assert.Equal($"Symbols | {root.Guid}", withGuids[0].Text);
    }

    [Fact]
    public void DescriptionsAreShownOnlyWithRootAndSmallOutput()
    {
        var noRoot = FolderTreeView.Levels(Tree, null, 2, false, false);
        Assert.DoesNotContain(noRoot, line => line.Text.Contains("Passives", StringComparison.Ordinal));

        var many = Enumerable.Range(0, FolderTreeView.DescriptionsLimit + 1)
            .Select(number => new FolderNode(
                Guid: number.ToString(), Name: $"F{number}", Path: $@"Root\F{number}",
                Description: "description", ParentGuid: null, FolderTypeGuid: null))
            .Append(new FolderNode("r", "Root", "Root", "root", null, null))
            .ToList();

        var big = FolderTreeView.Levels(many, many[^1], 2, false, false);
        Assert.DoesNotContain(big, line => line.Text.Contains("description", StringComparison.Ordinal));
    }

    [Fact]
    public void NameSearchFindsAtAnyDepthWithFullPaths()
    {
        var lines = FolderTreeView.ByName(Tree, null, "resistor", includeSystem: false, includeGuids: false);

        Assert.Equal(
            [@"Components\Passive Components\Resistors", @"Symbols\Resistors"],
            lines.Select(line => line.Text));

        var inBranch = FolderTreeView.ByName(Tree, Folder("Symbols"), "RESISTOR", false, false);
        Assert.Equal([@"Symbols\Resistors"], inBranch.Select(line => line.Text));

        Assert.Empty(FolderTreeView.ByName(Tree, null, "capacitor plus", false, false));
    }

    [Fact]
    public void NameSearchHidesSystemFoldersUnlessAsked()
    {
        Assert.Empty(FolderTreeView.ByName(Tree, null, "Datasheets", includeSystem: false, includeGuids: false));

        var lines = FolderTreeView.ByName(Tree, null, "Datasheets", includeSystem: true, includeGuids: false);
        Assert.Equal([@"Components\Passive Components\Resistors\Datasheets"], lines.Select(line => line.Text));
    }

    [Fact]
    public void FullTreeAtTopLevelsIsSmall()
    {
        // The real tree of ~485 folders: the two top levels fit into thousands of characters, not 64 000.
        var big = Enumerable.Range(0, 20).SelectMany(top => Enumerable.Range(0, 20).Select(child => (top, child)))
            .Select(pair => new FolderNode($"g{pair.top}-{pair.child}", $"Sub{pair.child}", $@"Top{pair.top}\Sub{pair.child}", "description", null, null))
            .Concat(Enumerable.Range(0, 20).Select(top => new FolderNode($"t{top}", $"Top{top}", $"Top{top}", "description", null, null)))
            .ToList();

        var lines = FolderTreeView.Levels(big, null, 2, false, false);
        Assert.Equal(420, lines.Count);

        var one = FolderTreeView.Levels(big, null, 1, false, false);
        Assert.Equal(20, one.Count);
        Assert.All(one, line => Assert.Equal(20, line.Hidden));
    }
}
