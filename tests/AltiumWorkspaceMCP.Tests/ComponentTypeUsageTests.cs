using AltiumWorkspaceMCP.Vault;
using Xunit;

namespace AltiumWorkspaceMCP.Tests;

public class ComponentTypeUsageTests
{
    private static readonly ComponentTypeNode Passive = new("g1", "Passive", "Passive", null);
    private static readonly ComponentTypeNode Resistors = new("g2", "Resistors", "Passive\\Resistors", "g1");
    private static readonly ComponentTypeNode Chip = new("g3", "Chip", "Passive\\Resistors\\Chip", "g2");
    private static readonly ComponentTypeNode Caps = new("g4", "Capacitors", "Passive\\Capacitors", "g1");
    private static readonly ComponentTypeNode[] All = [Passive, Resistors, Chip, Caps];

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoTemplates =
        new Dictionary<string, IReadOnlyList<string>>();

    [Fact]
    public void Subtree_TakesTypeAndDescendantsOnly()
    {
        var subtree = ComponentTypeUsage.Subtree(All, Resistors);

        Assert.Equal(
            ["Passive\\Resistors", "Passive\\Resistors\\Chip"],
            subtree.Select(node => node.Path).OrderBy(path => path));
    }

    [Fact]
    public void Find_CountsDetailsInNestedTypes()
    {
        var counts = new Dictionary<string, int> { ["passive\\resistors\\chip"] = 42, ["passive\\capacitors"] = 7 };

        var lines = ComponentTypeUsage.Find(ComponentTypeUsage.Subtree(All, Resistors), counts, NoTemplates);

        var line = Assert.Single(lines);
        Assert.Equal("Passive\\Resistors\\Chip", line.Path);
        Assert.Equal(42, line.Details);
    }

    [Fact]
    public void Find_CountsTemplatesOfSubtreeOnly()
    {
        var templates = new Dictionary<string, IReadOnlyList<string>>
        {
            ["g3"] = ["R template"],
            ["g4"] = ["C template"],
        };

        var lines = ComponentTypeUsage.Find(
            ComponentTypeUsage.Subtree(All, Resistors), new Dictionary<string, int>(), templates);

        Assert.Equal(["R template"], Assert.Single(lines).Templates);
    }

    [Fact]
    public void Find_EmptySubtree_HasNoLines()
    {
        var counts = new Dictionary<string, int> { ["passive\\capacitors"] = 7 };

        Assert.Empty(ComponentTypeUsage.Find(ComponentTypeUsage.Subtree(All, Resistors), counts, NoTemplates));
    }

    [Fact]
    public void DescribeRefusal_ShowsFirstTenAndHints()
    {
        var lines = Enumerable.Range(1, 12).Select(i => new TypeUsageLine($"T{i}", i, [])).ToList();

        string text = ComponentTypeUsage.DescribeRefusal("Root", lines);

        Assert.Contains("T10 → parts: 10", text);
        Assert.DoesNotContain("T11 →", text);
        Assert.Contains("2 more types", text);
        Assert.Contains("assign", text);
        Assert.Contains("set_type", text);
        Assert.Contains("with a delay", text);
    }
}
