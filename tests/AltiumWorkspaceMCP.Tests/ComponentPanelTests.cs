using System.Text.Json;
using AltiumWorkspaceMCP.Vault;
using AltiumWorkspaceMCP.Vault.Rest;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>An analog of the Components panel: filters, parameters from facets, the type tree, field names.</summary>
public sealed class ComponentPanelTests
{
    private const string ResistanceGuid = "B90F0DAE-B695-41F5-BCB0-0DE5F75C9E50";

    private static SearchFacet Facet(string field, params (string Value, int Count)[] counters) =>
        new(field, counters.Sum(counter => counter.Count), counters.Select(counter => new FacetCounter(counter.Value, counter.Count)).ToList(), false, null, null);

    private static PanelParameters ResistorParameters() => PanelParameters.FromFacets(
    [
        Facet(SearchFieldNames.Parameter("Value" + SearchFieldNames.TextMirrorSuffix), ("10k", 13), ("1k", 12)),
        new SearchFacet(SearchFieldNames.NumericParameter("Value", ResistanceGuid), 2266, [new("10000", 13)], true, "0", "10000000"),
        Facet(SearchFieldNames.Parameter("Case/Package"), ("0402", 1077), ("0603", 885)),
        Facet(SearchFieldNames.Parameter("ComponentType"), ("passive\\resistors\\", 55)),
        Facet(SearchFieldNames.Parameter("FootprintName1"), ("r 0603", 3)),
        Facet(SearchFieldNames.Core("Comment"), ("x", 1)),
    ]);

    // ── Filters ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Value=10k", "Value", FilterOperator.Equal, "10k")]
    [InlineData("Voltage Rating>=16", "Voltage Rating", FilterOperator.AtLeast, "16")]
    [InlineData("Tolerance <= 5%", "Tolerance", FilterOperator.AtMost, "5%")]
    [InlineData("Case/Package=0603", "Case/Package", FilterOperator.Equal, "0603")]
    public void FilterIsParsed(string text, string name, FilterOperator op, string value) =>
        Assert.Equal(new PanelFilter(name, op, value), PanelFilter.Parse(text));

    [Theory]
    [InlineData("Value")]
    [InlineData("=10k")]
    [InlineData("Value=")]
    public void BrokenFilterExplainsFormat(string text)
    {
        var error = Assert.Throws<ArgumentException>(() => PanelFilter.Parse(text));

        Assert.Contains("Format", error.Message);
    }

    [Fact]
    public void SystemFieldsAreNotParameters()
    {
        PanelParameters parameters = ResistorParameters();

        Assert.Equal(["Value", "Case/Package"], parameters.All.Select(parameter => parameter.Name));
    }

    [Fact]
    public void NumericAndMirrorFieldsMergeIntoOneParameter()
    {
        PanelParameter value = ResistorParameters().Find("value");

        Assert.True(value.IsNumeric);
        Assert.Equal(ResistanceGuid, value.TypeGuid);
        Assert.Equal(SearchFieldNames.Parameter("Value_T@x^"), value.TextField);
        Assert.Equal(2266, value.Filled);
    }

    [Fact]
    public void NumericEqualityUsesNumericFieldAndBaseUnits()
    {
        var condition = Assert.IsType<StrictCondition>(ResistorParameters().BuildCondition(PanelFilter.Parse("Value=10k")));

        Assert.Equal(SearchFieldNames.NumericParameter("Value", ResistanceGuid), condition.Field);
        Assert.Equal("10000", condition.Value);
    }

    [Fact]
    public void AtLeastBecomesInclusiveRangeWithoutUpperBound()
    {
        var condition = Assert.IsType<RangeCondition>(ResistorParameters().BuildCondition(PanelFilter.Parse("Value>=1k")));

        Assert.Equal("1000", condition.Min);
        Assert.Null(condition.Max);
        Assert.True(condition.MinInclusive);
    }

    [Fact]
    public void AtMostBecomesInclusiveRangeWithoutLowerBound()
    {
        var condition = Assert.IsType<RangeCondition>(ResistorParameters().BuildCondition(PanelFilter.Parse("Value<=4.7k")));

        Assert.Null(condition.Min);
        Assert.Equal("4700", condition.Max);
        Assert.True(condition.MaxInclusive);
    }

    [Fact]
    public void TextParameterIsComparedAsText()
    {
        PanelParameters parameters = ResistorParameters();

        var strict = Assert.IsType<StrictCondition>(parameters.BuildCondition(PanelFilter.Parse("Case/Package=0603")));
        Assert.Equal(SearchFieldNames.Parameter("Case/Package"), strict.Field);
        Assert.Equal("0603", strict.Value);

        var wildcard = Assert.IsType<WildcardCondition>(parameters.BuildCondition(PanelFilter.Parse("Case/Package=06*")));
        Assert.Equal("06*", wildcard.Value);
    }

    [Fact]
    public void RangeOnTextParameterIsRefusedWithAdvice()
    {
        var error = Assert.Throws<ArgumentException>(
            () => ResistorParameters().BuildCondition(PanelFilter.Parse("Case/Package>=5")));

        Assert.Contains("is text", error.Message);
        Assert.Contains("Case/Package=value", error.Message);
    }

    [Fact]
    public void NonNumericValueOfNumericParameterExplainsUnit()
    {
        var error = Assert.Throws<ArgumentException>(
            () => ResistorParameters().BuildCondition(PanelFilter.Parse("Value>=abc")));

        Assert.Contains("is not a number", error.Message);
        Assert.Contains("Resistance", error.Message);
    }

    [Fact]
    public void UnknownParameterSuggestsNearestNames()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => ResistorParameters().BuildCondition(PanelFilter.Parse("Valeu=10k")));

        Assert.Contains("Similar: Value", error.Message);
    }

    [Fact]
    public void NumberIsFormattedInvariantly() =>
        Assert.Equal("1E-07", PanelParameters.FormatNumber(1e-7));

    // ── Field names and request ──────────────────────────────────────────────

    [Fact]
    public void FieldNameIsParsedIntoParameterAndKind()
    {
        ParsedFieldName plain = SearchFieldNames.Parse(SearchFieldNames.Parameter("Case/Package"))!;
        Assert.Equal((FieldGroup.Parameters, "Case/Package", FieldKind.Plain), (plain.Group, plain.Parameter, plain.Kind));

        ParsedFieldName mirror = SearchFieldNames.Parse(SearchFieldNames.Parameter("Value_T@x^"))!;
        Assert.Equal(("Value", FieldKind.TextMirror), (mirror.Parameter, mirror.Kind));

        ParsedFieldName numeric = SearchFieldNames.Parse(SearchFieldNames.NumericParameter("Value", ResistanceGuid.ToLowerInvariant()))!;
        Assert.Equal(("Value", FieldKind.Numeric, ResistanceGuid), (numeric.Parameter, numeric.Kind, numeric.TypeGuid));

        ParsedFieldName core = SearchFieldNames.Parse(SearchFieldNames.Core("FolderFullPath"))!;
        Assert.Equal((FieldGroup.RevisionCore, "FolderFullPath"), (core.Group, core.Parameter));

        Assert.Null(SearchFieldNames.Parse("Unknown"));
    }

    [Fact]
    public void UnescapeReversesEscape()
    {
        const string name = "LCSC Part#/Value_T@x^ (V)";
        Assert.Equal(name, SearchFieldNames.Unescape(SearchFieldNames.Escape(name)));
    }

    [Fact]
    public void RangeConditionSerializesBoundsAndInclusiveFlags()
    {
        using JsonDocument document = JsonDocument.Parse(new RangeCondition("F", "1E-06", null).ToJson().ToJsonString());
        JsonElement root = document.RootElement;

        Assert.Equal("DtoSearchConditionRangeQuery", root.GetProperty("$type").GetString());
        Assert.Equal("F", root.GetProperty("Field").GetString());
        Assert.Equal("1E-06", root.GetProperty("Min").GetString());
        Assert.False(root.TryGetProperty("Max", out _));
        Assert.True(root.GetProperty("MinInclusive").GetBoolean());
        Assert.True(root.GetProperty("MaxInclusive").GetBoolean());
    }

    // ── Type tree ────────────────────────────────────────────────────────────

    private static readonly ComponentTypeNode[] Nodes =
    [
        new("g1", "Passive", "Passive", null),
        new("g2", "Resistors", "Passive\\Resistors", "g1"),
        new("g3", "Resistors Small", "Passive\\Resistors\\Resistors Small", "g2"),
        new("g4", "Resistors Big", "Passive\\Resistors\\Resistors Big", "g2"),
        new("g5", "Capacitors", "Passive\\Capacitors", "g1"),
        new("g6", "Connectors", "Connectors", null),
        new("g7", "Empty", "Empty", null),
        new("g8", "Beads", "Passive\\Resistors\\Resistors Big\\Beads", "g4"),
    ];

    private static readonly Dictionary<string, int> Counts = new()
    {
        ["passive\\resistors"] = 55,
        ["passive\\resistors\\resistors small"] = 2267,
        ["passive\\resistors\\resistors big"] = 5,
        ["passive\\capacitors"] = 319,
        ["connectors"] = 704,
        ["passive\\resistors\\resistors big\\beads"] = 3,
    };

    [Fact]
    public void TreeWithoutRootShowsTwoLevelsWithTotalsAndHiddenCount()
    {
        var lines = ComponentTypeTree.Build(Nodes, Counts, under: null, depth: 2);

        Assert.Equal(
            ["Connectors", "Passive", "Passive\\Capacitors", "Passive\\Resistors"],
            lines.Select(line => line.Path));

        TypeLine passive = lines.Single(line => line.Path == "Passive");
        Assert.Equal((0, 2649), (passive.Own, passive.Total));
        Assert.Equal(3, passive.Hidden); // Resistors Small, Resistors Big, Beads are deeper than two levels

        TypeLine resistors = lines.Single(line => line.Path == "Passive\\Resistors");
        Assert.Equal((55, 2330), (resistors.Own, resistors.Total));
    }

    [Fact]
    public void TreeUnderShowsTypeAndTwoLevelsBelow()
    {
        var lines = ComponentTypeTree.Build(Nodes, Counts, under: "Passive\\Resistors", depth: 2);

        Assert.Equal(
            ["Passive\\Resistors", "Passive\\Resistors\\Resistors Big", "Passive\\Resistors\\Resistors Big\\Beads", "Passive\\Resistors\\Resistors Small"],
            lines.Select(line => line.Path));
        Assert.All(lines, line => Assert.Equal(0, line.Hidden));
    }

    [Fact]
    public void TypeFromFacetWithoutTagIsStillShown()
    {
        var counts = new Dictionary<string, int>(Counts) { ["removed\\type"] = 4 };

        var lines = ComponentTypeTree.Build(Nodes, counts, under: null, depth: 2);

        Assert.Contains(lines, line => line.Path == "removed\\type" && line.Total == 4);
    }

    [Theory]
    [InlineData("Passive\\Resistors\\Resistors Small", "g3")]
    [InlineData("passive/resistors/resistors small/", "g3")]
    [InlineData("Resistors Small", "g3")]
    [InlineData("resistors small", "g3")]
    [InlineData("g5", "g5")]
    [InlineData("Capacitors", "g5")]
    [InlineData("Connect", "g6")]
    public void TypeIsResolvedLeniently(string input, string expectedGuid) =>
        Assert.Equal(expectedGuid, ComponentTypeTree.Resolve(Nodes, input).Guid);

    [Fact]
    public void AmbiguousTypeListsCandidates()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ComponentTypeTree.Resolve(Nodes, "Res"));

        Assert.Contains("Passive\\Resistors\\Resistors Small", error.Message);
        Assert.Contains("full path", error.Message);
    }

    [Fact]
    public void UnknownTypeSuggestsNearest()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ComponentTypeTree.Resolve(Nodes, "Pasive"));

        Assert.Contains("Passive", error.Message);
        Assert.Contains("action=types", error.Message);
    }

    [Fact]
    public void DescendantsIncludeTypeItselfAndAllNested()
    {
        ComponentTypeNode resistors = Nodes.Single(node => node.Guid == "g2");

        Assert.Equal(
            ["Passive\\Resistors", "Passive\\Resistors\\Resistors Small", "Passive\\Resistors\\Resistors Big", "Passive\\Resistors\\Resistors Big\\Beads"],
            ComponentTypeTree.WithDescendants(Nodes, resistors));
    }
}
