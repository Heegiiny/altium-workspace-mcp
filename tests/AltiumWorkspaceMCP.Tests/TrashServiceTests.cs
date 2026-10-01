using AltiumWorkspaceMCP.Vault;
using Xunit;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>Selection by time/type, pages and assembly of trash usage.</summary>
public sealed class TrashServiceTests
{
    private static TrashItemCandidate Candidate(
        string guid, string hrid, string? contentType, string path, DateTimeOffset deletedAt,
        string? deletedWithFolderPath = null) =>
        new(guid, hrid, contentType, path, deletedAt, deletedWithFolderPath);

    private static readonly DateTimeOffset Day1 = new(2026, 9, 22, 19, 50, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day1Mid = new(2026, 9, 22, 19, 52, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day1End = new(2026, 9, 22, 19, 53, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day2 = new(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);

    // ── TrashSelection: selection before the usage count ───────────────────────

    [Fact]
    public void SelectItems_FiltersByPathContains()
    {
        var all = new[]
        {
            Candidate("g1", "SYM-0001", "altium-symbol", @"Components\Resistors", Day1),
            Candidate("g2", "SYM-0002", "altium-symbol", @"Components\Capacitors", Day1),
        };

        var selected = TrashSelection.SelectItems(all, "Resistors", null, null, null);

        Assert.Equal(["g1"], selected.Select(candidate => candidate.Guid));
    }

    [Fact]
    public void SelectItems_FiltersByDeletionWindow()
    {
        var all = new[]
        {
            Candidate("before", "H1", null, @"A", Day1 - TimeSpan.FromMinutes(1)),
            Candidate("inside-start", "H2", null, @"A", Day1),
            Candidate("inside-mid", "H3", null, @"A", Day1Mid),
            Candidate("inside-end", "H4", null, @"A", Day1End),
            Candidate("after", "H5", null, @"A", Day1End + TimeSpan.FromMinutes(1)),
        };

        var selected = TrashSelection.SelectItems(all, null, Day1, Day1End, null);

        Assert.Equal(
            ["inside-start", "inside-mid", "inside-end"],
            selected.Select(candidate => candidate.Guid));
    }

    [Fact]
    public void SelectItems_FiltersByContentTypeCaseInsensitively()
    {
        var all = new[]
        {
            Candidate("g1", "SYM-1", "altium-symbol", "A", Day2),
            Candidate("g2", "PCC-1", "altium-pcb-component", "A", Day2),
            Candidate("g3", "DSH-1", "altium-datasheet", "A", Day2),
        };

        var selected = TrashSelection.SelectItems(all, null, null, null, ["Altium-Symbol", "altium-pcb-component"]);

        Assert.Equal(["g1", "g2"], selected.Select(candidate => candidate.Guid).OrderBy(guid => guid));
    }

    [Fact]
    public void SelectItems_EmptyContentTypesMeansNoFilter()
    {
        var all = new[] { Candidate("g1", "SYM-1", "altium-symbol", "A", Day2) };

        var selected = TrashSelection.SelectItems(all, null, null, null, []);

        Assert.Single(selected);
    }

    [Fact]
    public void SelectItems_ItemWithoutContentTypeExcludedByTypeFilter()
    {
        var all = new[] { Candidate("g1", "H1", null, "A", Day2) };

        var selected = TrashSelection.SelectItems(all, null, null, null, ["altium-symbol"]);

        Assert.Empty(selected);
    }

    [Fact]
    public void SelectItems_OrderedByPathThenHrid()
    {
        var all = new[]
        {
            Candidate("g1", "HRID-B", null, @"Z\Folder", Day2),
            Candidate("g2", "HRID-A", null, @"A\Folder", Day2),
            Candidate("g3", "HRID-A", null, @"A\Folder\Sub", Day2),
        };

        var selected = TrashSelection.SelectItems(all, null, null, null, null);

        Assert.Equal(["g2", "g3", "g1"], selected.Select(candidate => candidate.Guid));
    }

    // ── TrashPageBuilder: pages, onlyUsed, the time limit ───────────────────────

    private static readonly UsageSummary OneComponent = UsageSummary.Of(["CMP-001"]);

    private static ItemUsage Used() => new(OneComponent, UsageSummary.Empty);

    private static ItemUsage NotUsed() => new(UsageSummary.Empty, UsageSummary.Empty);

    [Fact]
    public void Build_PlainPage_TakesExactlyLimitFromOffset()
    {
        var candidates = Enumerable.Range(0, 10)
            .Select(i => Candidate($"g{i}", $"H{i}", null, "A", Day2))
            .ToList();

        var page = TrashPageBuilder.Build(candidates, offset: 2, limit: 3, onlyUsed: false, _ => NotUsed());

        Assert.Equal(["g2", "g3", "g4"], page.Items.Select(item => item.Guid));
        Assert.Equal(5, page.NextOffset);
        Assert.False(page.TimedOut);
    }

    [Fact]
    public void Build_LastPage_HasNullNextOffset()
    {
        var candidates = Enumerable.Range(0, 5)
            .Select(i => Candidate($"g{i}", $"H{i}", null, "A", Day2))
            .ToList();

        var page = TrashPageBuilder.Build(candidates, offset: 3, limit: 50, onlyUsed: false, _ => NotUsed());

        Assert.Equal(["g3", "g4"], page.Items.Select(item => item.Guid));
        Assert.Null(page.NextOffset);
    }

    [Fact]
    public void Build_SumOfAllPagesEqualsTotal_WithoutOnlyUsed()
    {
        var candidates = Enumerable.Range(0, 23)
            .Select(i => Candidate($"g{i}", $"H{i}", null, "A", Day2))
            .ToList();

        var collected = new List<string>();
        int? offset = 0;

        while (offset is { } current)
        {
            var page = TrashPageBuilder.Build(candidates, current, limit: 7, onlyUsed: false, _ => NotUsed());
            collected.AddRange(page.Items.Select(item => item.Guid));
            offset = page.NextOffset;
        }

        Assert.Equal(candidates.Count, collected.Count);
        Assert.Equal(candidates.Select(c => c.Guid), collected);
        Assert.Equal(collected.Distinct().Count(), collected.Count);
    }

    [Fact]
    public void Build_OnlyUsed_SkipsUnusedWithoutCountingThemTowardLimit()
    {
        var candidates = Enumerable.Range(0, 6)
            .Select(i => Candidate($"g{i}", $"H{i}", null, "A", Day2))
            .ToList();

        // Only g1 and g4 are used — the rest do not count towards the limit.
        var page = TrashPageBuilder.Build(
            candidates, offset: 0, limit: 2, onlyUsed: true,
            candidate => candidate.Guid is "g1" or "g4" ? Used() : NotUsed());

        Assert.Equal(["g1", "g4"], page.Items.Select(item => item.Guid));
        Assert.Equal(5, page.NextOffset); // g4 is index 4, the next checked one would be 5
    }

    [Fact]
    public void Build_OnlyUsed_NoMatchesReachesEndWithNullNextOffset()
    {
        var candidates = Enumerable.Range(0, 4)
            .Select(i => Candidate($"g{i}", $"H{i}", null, "A", Day2))
            .ToList();

        var page = TrashPageBuilder.Build(candidates, offset: 0, limit: 50, onlyUsed: true, _ => NotUsed());

        Assert.Empty(page.Items);
        Assert.Null(page.NextOffset);
        Assert.False(page.TimedOut);
    }

    [Fact]
    public void Build_MissingUsageStopsAsTimedOutAtThatPosition()
    {
        var candidates = Enumerable.Range(0, 5)
            .Select(i => Candidate($"g{i}", $"H{i}", null, "A", Day2))
            .ToList();

        // "The time budget is exhausted" on the third item (index 2) — usageLookup returns null.
        var page = TrashPageBuilder.Build(
            candidates, offset: 0, limit: 50, onlyUsed: false,
            candidate => candidate.Guid == "g2" ? null : NotUsed());

        Assert.Equal(["g0", "g1"], page.Items.Select(item => item.Guid));
        Assert.Equal(2, page.NextOffset);
        Assert.True(page.TimedOut);
    }

    [Fact]
    public void Build_OffsetBeyondCandidates_ReturnsEmptyPage()
    {
        var candidates = new[] { Candidate("g0", "H0", null, "A", Day2) };

        var page = TrashPageBuilder.Build(candidates, offset: 5, limit: 10, onlyUsed: false, _ => NotUsed());

        Assert.Empty(page.Items);
        Assert.Null(page.NextOffset);
    }

    // ── UsageSummary ─────────────────────────────────────────────────────────

    [Fact]
    public void UsageSummary_EmptyForNoHrids()
    {
        Assert.Same(UsageSummary.Empty, UsageSummary.Of([]));
    }

    [Fact]
    public void UsageSummary_CountsAllButShowsFirstThreeAlphabetically()
    {
        var summary = UsageSummary.Of(["CMP-005", "CMP-001", "CMP-003", "CMP-002", "CMP-004"]);

        Assert.Equal(5, summary.Count);
        Assert.Equal(["CMP-001", "CMP-002", "CMP-003"], summary.SampleHrids);
    }

    [Fact]
    public void UsageSummary_DeduplicatesCaseInsensitively()
    {
        var summary = UsageSummary.Of(["CMP-001", "cmp-001", "CMP-001"]);

        Assert.Equal(1, summary.Count);
    }

    // ── TemplateReferenceIndex.FindTemplateHrids ────────────────────────────────

    [Fact]
    public void FindTemplateHrids_ReturnsDistinctSortedHridsRegardlessOfRole()
    {
        var references = new[]
        {
            new TemplateModelReference("TPL-002", "tpl2", "model1", LinkRole.Symbol, "symbol"),
            new TemplateModelReference("TPL-001", "tpl1", "model1", LinkRole.Footprint, "footprint"),
            new TemplateModelReference("TPL-003", "tpl3", "other", LinkRole.Symbol, "symbol"),
        };

        var hrids = TemplateReferenceIndex.FindTemplateHrids(references, "model1");

        Assert.Equal(["TPL-001", "TPL-002"], hrids);
    }

    // ── TrashPaths.FindNearestDeletedAncestor: deletion time by folder ─────

    [Fact]
    public void FindNearestDeletedAncestor_ItemsOwnFolderDeleted_ReturnsThatFolder()
    {
        var nodes = new Dictionary<string, TrashNode>(StringComparer.OrdinalIgnoreCase)
        {
            ["root"] = new TrashNode("Components", null),
            ["folder"] = new TrashNode("Resistors", "root"),
        };
        var pathByGuid = TrashPaths.BuildPathMap(nodes);
        var deletedAt = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase)
        {
            ["folder"] = Day1Mid,
        };

        var ancestor = TrashPaths.FindNearestDeletedAncestor("folder", nodes, deletedAt, pathByGuid);

        Assert.NotNull(ancestor);
        Assert.Equal("folder", ancestor!.Value.Guid);
        Assert.Equal(Day1Mid, ancestor.Value.DeletedAt);
        Assert.Equal(@"Components\Resistors", ancestor.Value.Path);
    }

    [Fact]
    public void FindNearestDeletedAncestor_WalksUpToDeletedGrandparent()
    {
        var nodes = new Dictionary<string, TrashNode>(StringComparer.OrdinalIgnoreCase)
        {
            ["root"] = new TrashNode("Components", null),
            ["mid"] = new TrashNode("Passive", "root"),
            ["folder"] = new TrashNode("Resistors", "mid"),
        };
        var pathByGuid = TrashPaths.BuildPathMap(nodes);
        var deletedAt = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase)
        {
            ["root"] = Day1,
        };

        var ancestor = TrashPaths.FindNearestDeletedAncestor("folder", nodes, deletedAt, pathByGuid);

        Assert.NotNull(ancestor);
        Assert.Equal("root", ancestor!.Value.Guid);
        Assert.Equal(Day1, ancestor.Value.DeletedAt);
    }

    [Fact]
    public void FindNearestDeletedAncestor_PrefersNearerOverFartherDeletedAncestor()
    {
        var nodes = new Dictionary<string, TrashNode>(StringComparer.OrdinalIgnoreCase)
        {
            ["root"] = new TrashNode("Components", null),
            ["folder"] = new TrashNode("Resistors", "root"),
        };
        var pathByGuid = TrashPaths.BuildPathMap(nodes);
        var deletedAt = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase)
        {
            ["root"] = Day1,
            ["folder"] = Day1Mid,
        };

        var ancestor = TrashPaths.FindNearestDeletedAncestor("folder", nodes, deletedAt, pathByGuid);

        Assert.Equal("folder", ancestor!.Value.Guid);
        Assert.Equal(Day1Mid, ancestor.Value.DeletedAt);
    }

    [Fact]
    public void FindNearestDeletedAncestor_NoDeletedAncestor_ReturnsNull()
    {
        var nodes = new Dictionary<string, TrashNode>(StringComparer.OrdinalIgnoreCase)
        {
            ["root"] = new TrashNode("Components", null),
            ["folder"] = new TrashNode("Resistors", "root"),
        };
        var pathByGuid = TrashPaths.BuildPathMap(nodes);
        var deletedAt = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        var ancestor = TrashPaths.FindNearestDeletedAncestor("folder", nodes, deletedAt, pathByGuid);

        Assert.Null(ancestor);
    }

    // ── A deletion window sees items with a stale LastModifiedAt in a deleted folder ─────────

    [Fact]
    public void ItemWithStaleLastModifiedAt_InFolderDeletedInsideWindow_FallsIntoThatWindow()
    {
        // The item was edited long ago (Day2 is "in the future" relative to the deletion window — like an item
        // the server has not touched for years), but its folder went to the trash inside the window —
        // the nearest deleted ancestor gives the item this time.
        var nodes = new Dictionary<string, TrashNode>(StringComparer.OrdinalIgnoreCase)
        {
            ["root"] = new TrashNode("Components", null),
            ["folder"] = new TrashNode("Models", "root"),
        };
        var pathByGuid = TrashPaths.BuildPathMap(nodes);
        var deletedAt = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase)
        {
            ["folder"] = Day1Mid,
        };

        TrashPaths.DeletedAncestor? ancestor = TrashPaths.FindNearestDeletedAncestor("folder", nodes, deletedAt, pathByGuid);
        DateTimeOffset staleOwnLastModifiedAt = Day2;
        DateTimeOffset resolvedDeletedAt = ancestor?.DeletedAt ?? staleOwnLastModifiedAt;

        var candidate = Candidate("model", "SYM-0004", "altium-symbol", @"Components\Models", resolvedDeletedAt, ancestor?.Path);

        var selected = TrashSelection.SelectItems([candidate], null, Day1, Day1End, null);

        Assert.Single(selected);
        Assert.Equal(Day1Mid, selected[0].DeletedAt);
        Assert.Equal(@"Components\Models", selected[0].DeletedWithFolderPath);
    }
}
