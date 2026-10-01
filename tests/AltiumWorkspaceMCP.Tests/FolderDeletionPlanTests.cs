using AltiumWorkspaceMCP.Soap.Vault;
using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>Counting the folder deletion volume and the refusal text on external references.</summary>
public sealed class FolderDeletionPlanTests
{
    private static FolderNode Folder(string path) => new(
        Guid.NewGuid().ToString(), path[(path.LastIndexOf('\\') + 1)..], path, null, null, null);

    private static ALU_Item Item(string hrid) => new() { GUID = Guid.NewGuid().ToString(), HRID = hrid };

    [Fact]
    public void ObjectCountIsSubtreeFoldersPlusItems()
    {
        var plan = new FolderService.FolderDeletionPlan(
            Folder(@"Components\Old"),
            [Folder(@"Components\Old"), Folder(@"Components\Old\Resistors")],
            [Item("CMP-001"), Item("CMP-002"), Item("CMP-003")]);

        // A folder deletion used to count as one object, which let the deletion of a whole tree
        // go through without confirmation: 2 folders + 3 items = 5, not 1.
        Assert.Equal(5, plan.ObjectCount);
    }

    [Fact]
    public void EmptyFolderCountsAsItself()
    {
        var plan = new FolderService.FolderDeletionPlan(Folder(@"Components\Empty"), [Folder(@"Components\Empty")], []);

        Assert.Equal(1, plan.ObjectCount);
    }

    [Fact]
    public void DescribeBlockedNamesModelUsersAndFolders()
    {
        var root = Folder(@"Components\Old\Resistors");
        var blocked = new[]
        {
            new BlockedModel(
                "guid-1", "SYM-000-0076", [new LiveUsage("CMP-000-00001", @"Components\Passive\Resistors")]),
        };

        string message = FolderService.DescribeBlocked(root, blocked);

        Assert.Contains(root.Path, message);
        Assert.Contains("SYM-000-0076", message);
        Assert.Contains("CMP-000-00001", message);
        Assert.Contains("vault_move_items", message);
        Assert.Contains("would break references", message);
    }

    [Fact]
    public void DescribeBlockedTruncatesLongUserLists()
    {
        var users = Enumerable.Range(1, 8).Select(i => new LiveUsage($"CMP-{i:000}", @"Components\X")).ToList();
        var blocked = new[] { new BlockedModel("guid-1", "PCC-0009", users) };

        string message = FolderService.DescribeBlocked(Folder(@"Components\Old"), blocked);

        Assert.Contains("8 parts", message);
        Assert.Contains("…", message);
        Assert.DoesNotContain("CMP-008", message);
    }
}
