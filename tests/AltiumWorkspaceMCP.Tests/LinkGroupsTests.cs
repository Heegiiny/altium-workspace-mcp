using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Tests;

public sealed class LinkGroupsTests
{
    private static LinkGroupSpec Group(string[] components, params (string Role, string? Target)[] links) =>
        new(components, links.Select(link => new LinkTargetSpec(link.Role, link.Target)).ToList());

    [Fact]
    public void ValidateNormalizesRoles()
    {
        var groups = LinkGroups.Validate([Group(["CMP-1"], ("footprint", "PCC-1"), ("symbol", "SYM-1"))]);

        Assert.Equal([LinkRole.Footprint, LinkRole.Symbol], groups[0].Links.Select(link => link.Role));
    }

    [Fact]
    public void ValidateRejectsEmptyInput()
    {
        Assert.Throws<ArgumentException>(() => LinkGroups.Validate([]));
        Assert.Throws<ArgumentException>(() => LinkGroups.Validate([Group([], ("footprint", "PCC-1"))]));
        Assert.Throws<ArgumentException>(() => LinkGroups.Validate([Group(["CMP-1"])]));
    }

    [Fact]
    public void ValidateRejectsRepeatedRoleInGroup()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            LinkGroups.Validate([Group(["CMP-1"], ("footprint", "PCC-1"), ("pcb", "PCC-2"))]));

        Assert.Contains("Group 1", error.Message);
        Assert.Contains("footprint", error.Message);
    }

    [Fact]
    public void OverlapOnSameRoleIsReported()
    {
        var groups = LinkGroups.Validate([
            Group(["CMP-1", "CMP-2"], ("footprint", "PCC-1")),
            Group(["cmp-2", "CMP-3"], ("footprint", "PCC-2")),
        ]);

        var overlap = Assert.Single(LinkGroups.FindOverlaps(groups));
        Assert.Equal("CMP-2", overlap.Component);
        Assert.Equal([1, 2], overlap.Groups);
        Assert.Contains("CMP-2", LinkGroups.DescribeOverlaps([overlap]));
    }

    [Fact]
    public void OverlapIsFoundThroughAlias()
    {
        var groups = LinkGroups.Validate([
            Group(["CMP-1"], ("footprint", "PCC-1")),
            Group(["GUID-OF-CMP-1"], ("footprint", "PCC-2")),
        ]);

        Assert.Empty(LinkGroups.FindOverlaps(groups));
        Assert.Single(LinkGroups.FindOverlaps(groups, item => "ONE"));
    }

    [Fact]
    public void DifferentRolesOfOneComponentAreNotAnOverlap()
    {
        var groups = LinkGroups.Validate([
            Group(["CMP-1"], ("footprint", "PCC-1")),
            Group(["CMP-1"], ("symbol", "SYM-1")),
        ]);

        Assert.Empty(LinkGroups.FindOverlaps(groups));
        Assert.Equal(1, LinkGroups.CountAffected(groups));
    }

    [Fact]
    public void AffectedIsSumOfDistinctComponentsAcrossGroups()
    {
        var groups = LinkGroups.Validate([
            Group(["CMP-1", "CMP-2", "cmp-2"], ("footprint", "PCC-1")),
            Group(["CMP-3", "CMP-4"], ("footprint", "PCC-2")),
        ]);

        Assert.Equal(4, LinkGroups.CountAffected(groups));
    }
}
