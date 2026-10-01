using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>Paths and the restore order by the tree of live and deleted folders.</summary>
public sealed class TrashPathsTests
{
    // Components (live)
    //   └─ Passive Components (deleted)
    //        ├─ Resistors (deleted)
    //        │    └─ SMD (deleted)
    //        └─ Capacitors (deleted)
    private const string Root = "R00000000-0000-0000-0000-000000000000";
    private const string Passive = "R00000000-0000-0000-0000-000000000001";
    private const string Resistors = "R00000000-0000-0000-0000-000000000002";
    private const string Smd = "R00000000-0000-0000-0000-000000000003";
    private const string Capacitors = "R00000000-0000-0000-0000-000000000004";

    private static readonly IReadOnlyDictionary<string, TrashNode> Nodes = new Dictionary<string, TrashNode>
    {
        [Root] = new TrashNode("Components", null),
        [Passive] = new TrashNode("Passive Components", Root),
        [Resistors] = new TrashNode("Resistors", Passive),
        [Smd] = new TrashNode("SMD", Resistors),
        [Capacitors] = new TrashNode("Capacitors", Passive),
    };

    [Fact]
    public void PathIsBuiltFromLiveAndDeletedParentsTogether()
    {
        string path = TrashPaths.PathOf(Smd, Nodes);

        Assert.Equal(@"Components\Passive Components\Resistors\SMD", path);
    }

    [Fact]
    public void MissingParentStopsAtWhatIsKnown()
    {
        var orphan = new Dictionary<string, TrashNode>
        {
            ["X"] = new TrashNode("Orphan", "unknown-parent"),
        };

        Assert.Equal("Orphan", TrashPaths.PathOf("X", orphan));
    }

    [Fact]
    public void BuildPathMapCoversEveryNode()
    {
        var map = TrashPaths.BuildPathMap(Nodes);

        Assert.Equal(5, map.Count);
        Assert.Equal(@"Components\Passive Components\Capacitors", map[Capacitors]);
    }

    [Fact]
    public void RestoreOrderIsParentsBeforeChildren()
    {
        var pathByGuid = TrashPaths.BuildPathMap(Nodes);

        // The input order is deliberately shuffled: the deepest node first.
        var ordered = TrashPaths.OrderParentsFirst([Smd, Capacitors, Passive, Resistors], pathByGuid);

        Assert.Equal([Passive, Capacitors, Resistors, Smd], ordered);
    }

    [Fact]
    public void EqualDepthOrdersAlphabeticallyForDeterminism()
    {
        var pathByGuid = TrashPaths.BuildPathMap(Nodes);

        var ordered = TrashPaths.OrderParentsFirst([Resistors, Capacitors], pathByGuid);

        Assert.Equal([Capacitors, Resistors], ordered);
    }
}
