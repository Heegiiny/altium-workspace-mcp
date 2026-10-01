using System.Text.Encodings.Web;
using System.Text.Json;
using AltiumWorkspaceMCP.Vault;
using AltiumWorkspaceMCP.Vault.Rest;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>Type summary: shares, folding rare parameters, a top with "[+N]", parsing documents.</summary>
public sealed class TypeOverviewTests
{
    private const string P = SearchFieldNames.ParametersGroup;

    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static SearchDocument Document(string? type, string? folder, string? mpn, string? lcsc, string? footprint)
    {
        var fields = new Dictionary<string, string>();

        void Put(string field, string? value)
        {
            if (value is not null)
            {
                fields[field] = value;
            }
        }

        Put(SearchFieldNames.Parameter("ComponentType"), type);
        Put(SearchFieldNames.Core("FolderFullPath"), folder);
        Put(SearchFieldNames.Parameter(TypeOverview.MpnName), mpn);
        Put(SearchFieldNames.Parameter(TypeOverview.LcscName), lcsc);
        Put(SearchFieldNames.FootprintName(1), footprint);
        return new SearchDocument(0, fields);
    }

    private static SearchFacet Facet(string name, int hits, params (string Value, int Count)[] counters) =>
        new(name, hits, counters.Select(counter => new FacetCounter(counter.Value, counter.Count)).ToList(), false, null, null);

    // ── Shares ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1995, 2025, 98.5)]
    [InlineData(0, 100, 0)]
    [InlineData(1, 3, 33.3)]
    [InlineData(2, 3, 66.7)]
    [InlineData(5, 0, 0)]
    public void PercentRoundsToOneDecimalAndSurvivesEmptyTotal(long part, long total, double expected) =>
        Assert.Equal(expected, TypeOverview.Percent(part, total));

    [Fact]
    public void ShareUsesInvariantDecimalPoint() => Assert.Equal("1995 (98.5%)", TypeOverview.Share(1995, 2025));

    // ── Fill rate from a facet ───────────────────────────────────────────────

    [Fact]
    public void FilledCountSubtractsEmptyValuesOfFacet()
    {
        // FootprintDescription1: the index counts empty strings in the facet too.
        SearchFacet facet = Facet("FootprintDescription1" + P, 2024, ("", 1925), ("resistor 0805", 83), ("resistor 0603", 16));
        Assert.Equal(99, TypeOverview.FilledCount(facet));
    }

    [Fact]
    public void FilledCountOfMissingFacetIsZero() => Assert.Equal(0, TypeOverview.FilledCount(null));

    [Fact]
    public void ParameterTakesFillFromWidestFieldAndValuesFromWidestTextField()
    {
        // "Value" comes as three fields: numeric, the text "mirror" and the same-named text one on some parts.
        var facets = new[]
        {
            Facet(SearchFieldNames.Parameter("Value_T@x^"), 1995, ("10k", 12), ("1k", 8), ("100k", 8)),
            Facet(SearchFieldNames.Parameter("Value"), 29, ("1.2k", 2), ("1k", 2)),
            Facet(SearchFieldNames.NumericParameter("Value", "B90F0DAE-B695-41F5-BCB0-0DE5F75C9E50"), 1995, ("10000", 13)),
            Facet(SearchFieldNames.Parameter("ComponentType"), 2025, ("passive\\resistors\\chip\\", 2025)),
        };

        OverviewParameter value = Assert.Single(TypeOverview.FromFacets(facets));

        Assert.Equal("Value", value.Name);
        Assert.Equal(1995, value.Filled);
        Assert.Equal("10k (12)", value.Top[0]);
    }

    [Fact]
    public void TopValuesAreFiveWithoutEmptyOnes()
    {
        SearchFacet facet = Facet(
            SearchFieldNames.Parameter("Tolerance"), 100, ("", 40), ("1%", 30), ("5%", 10), ("2%", 8), ("0.1%", 6), ("10%", 4), ("20%", 2));

        OverviewParameter tolerance = Assert.Single(TypeOverview.FromFacets([facet]));

        Assert.Equal(60, tolerance.Filled);
        Assert.Equal(["1% (30)", "5% (10)", "2% (8)", "0.1% (6)", "10% (4)"], tolerance.Top);
    }

    [Fact]
    public void ParameterWithoutFilledValuesIsDropped() =>
        Assert.Empty(TypeOverview.FromFacets([Facet(SearchFieldNames.Parameter("Note"), 5, ("", 5))]));

    // ── Folding rare ones ────────────────────────────────────────────────────

    [Fact]
    public void FoldRareCollapsesParametersBelowTwoPercent()
    {
        var parameters = new[]
        {
            new OverviewParameter("Series", 71, []),
            new OverviewParameter("Value", 1995, []),
            new OverviewParameter("Height", 40, []),   // exactly 2 % — stays
            new OverviewParameter("Weight", 39, []),   // 1.95 % — rare
            new OverviewParameter("Radiation", 7, []),
        };

        (IReadOnlyList<OverviewParameter> shown, int rare) = TypeOverview.FoldRare(parameters, 2000);

        Assert.Equal(["Value", "Series", "Height"], shown.Select(parameter => parameter.Name));
        Assert.Equal(2, rare);
    }

    [Fact]
    public void FoldRareKeepsEverythingForEmptySelection()
    {
        (IReadOnlyList<OverviewParameter> shown, int rare) = TypeOverview.FoldRare([new OverviewParameter("A", 0, [])], 0);

        Assert.Single(shown);
        Assert.Equal(0, rare);
    }

    // ── Top with "[+N]" ──────────────────────────────────────────────────────

    [Fact]
    public void TopWithRestAddsCountOfHiddenEntries()
    {
        var counts = Enumerable.Range(1, 13).ToDictionary(number => $"f{number:00}", number => (long)number);

        var lines = TypeOverview.TopWithRest(counts, 10);

        Assert.Equal(11, lines.Count);
        Assert.Equal("f13 — 13", lines[0]);
        Assert.Equal("f04 — 4", lines[9]);
        Assert.Equal("[+3]", lines[10]);
    }

    [Fact]
    public void TopWithRestHasNoTailWhenEverythingFits() =>
        Assert.Equal(["a — 2", "b — 1"], TypeOverview.TopWithRest(new Dictionary<string, long> { ["b"] = 1, ["a"] = 2 }, 10));

    // ── Documents ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Passive\\Resistors\\Chip\\", "Passive")]
    [InlineData("Passive\\", "Passive")]
    [InlineData("Passive", "Passive")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void TopLevelTypeIsFirstSegment(string? path, string expected) => Assert.Equal(expected, TypeOverview.TopLevelType(path));

    [Fact]
    public void TallyCountsGapsFoldersAndTopTypes()
    {
        var documents = new[]
        {
            Document("Passive\\Resistors\\Chip\\", "Components\\R\\0603\\", "RC0603", "C1", "R 0603"),
            Document("Passive\\Resistors\\Chip\\", "Components\\R\\0603\\", "", "C2", "R 0603"),
            Document("Passive\\Capacitors\\", "Components\\C\\", null, null, "C 0402"),
            Document("Connectors\\", "Components\\J\\", "J1", "  ", null),
            Document(null, null, null, "C9", "X"),
        };

        DocumentTally tally = TypeOverview.Tally(documents);

        Assert.Equal(5, tally.Documents);
        Assert.Equal(1, tally.WithoutType);
        Assert.Equal(new OverviewGaps(WithoutMpn: 3, WithoutLcsc: 2, WithoutFootprint: 1), tally.Gaps);
        Assert.Equal(2, tally.Folders["Components\\R\\0603"]);
        Assert.Equal(1, tally.Folders["(no folder)"]);

        Assert.Equal(["Passive", "Connectors"], tally.TopTypes.Select(line => line.Type));
        Assert.Equal(new OverviewTypeLine("Passive", 3, 2), tally.TopTypes[0]);
        Assert.Equal(new OverviewTypeLine("Connectors", 1, 0), tally.TopTypes[1]);
    }

    [Fact]
    public void ReturnFieldsAreOnlyWhatOverviewNeeds() => Assert.Equal(5, TypeOverview.ReturnFields.Count);

    // ── Response ─────────────────────────────────────────────────────────────

    [Fact]
    public void ReportFitsBudgetAndFoldsRareParameters()
    {
        var parameters = Enumerable.Range(0, 80)
            .Select(number => new OverviewParameter(
                $"Parameter {number}", number < 40 ? 1500 - number : 10, ["aaaaaaaaaa (100)", "bbbbbbbbbb (90)", "cccccccccc (80)"]))
            .ToList();
        var tally = new DocumentTally(2000, 0, new Dictionary<string, long> { ["Components\\R"] = 2000 }, new OverviewGaps(900, 10, 1), []);
        var scope = new ScopeInfo("Passive\\Resistors\\Chip", null, null, []);
        var result = new OverviewResult(scope, 2000, tally, parameters, Truncated: false, ElapsedMs: 5);

        string json = JsonSerializer.Serialize(OverviewReport.Build(result, new PanelQuery("Chip"), 8000), Readable);

        Assert.True(json.Length < 8000, $"length {json.Length}");
        Assert.Contains("rare: 40 parameters", json);
        Assert.Contains("Parameter 0 — 1500 (75.0%)", json);
        Assert.DoesNotContain("Parameter 79", json);
    }

    [Fact]
    public void ReportCutsParametersThatDoNotFitBudget()
    {
        var parameters = Enumerable.Range(0, 60)
            .Select(number => new OverviewParameter($"Parameter {number}", 1500 - number, [new string('x', 150) + " (1)"]))
            .ToList();
        var tally = new DocumentTally(2000, 0, new Dictionary<string, long> { ["Components\\R"] = 2000 }, new OverviewGaps(0, 0, 0), []);
        var result = new OverviewResult(new ScopeInfo("Passive", null, null, []), 2000, tally, parameters, Truncated: false, ElapsedMs: 5);

        string json = JsonSerializer.Serialize(OverviewReport.Build(result, new PanelQuery("Passive"), 3000), Readable);

        Assert.True(json.Length < 3000, $"length {json.Length}");
        Assert.Contains("not shown", json);
    }

    [Fact]
    public void ReportWithoutTypeListsTopLevelTypesWithShareWithoutMpn()
    {
        var tally = new DocumentTally(
            10, 1, new Dictionary<string, long> { ["A"] = 10 }, new OverviewGaps(5, 0, 0),
            [new OverviewTypeLine("Passive", 8, 4), new OverviewTypeLine("Connectors", 1, 0)]);
        var result = new OverviewResult(new ScopeInfo(null, null, null, []), 10, tally, [], Truncated: false, ElapsedMs: 1);

        string json = JsonSerializer.Serialize(OverviewReport.Build(result, new PanelQuery(), 8000), Readable);

        Assert.Contains("Passive — 8, no MPN 4 (50.0%)", json);
        Assert.Contains("Parts without a type: 1", json);
    }
}
