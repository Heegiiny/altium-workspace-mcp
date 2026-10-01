using System.Text.Json;
using AltiumWorkspaceMCP.Soap.Vault;
using AltiumWorkspaceMCP.Vault;
using AltiumWorkspaceMCP.Vault.Rest;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>Footprints, folders and lifecycle states in vault_components.</summary>
public sealed class FootprintPanelTests
{
    private const string P = SearchFieldNames.ParametersGroup;
    private const string C = SearchFieldNames.RevisionCoreGroup;

    private static SearchDocument Document(params (string Field, string Value)[] fields) =>
        new(0, fields.ToDictionary(field => field.Field, field => field.Value));

    // ── Field names ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, "FootprintName1" + P)]
    [InlineData(14, "FootprintName14" + P)]
    public void FootprintNameFieldIsParameterOfParametersGroup(int number, string expected) =>
        Assert.Equal(expected, SearchFieldNames.FootprintName(number));

    [Fact]
    public void FootprintRevisionFieldMatchesRevisionIdFieldOfIndex() =>
        Assert.Equal("FootprintRevisionID8" + P, SearchFieldNames.FootprintRevision(8));

    [Fact]
    public void FolderFullPathFieldIsRevisionCoreField() =>
        Assert.Equal("FolderFullPath" + C, SearchFieldNames.Core("FolderFullPath"));

    [Fact]
    public void ReturnFieldsCoverAllNumbersOfRequestedKinds()
    {
        Assert.Empty(FootprintPanel.ReturnFields(names: false, revisions: false));
        Assert.Equal(14, FootprintPanel.ReturnFields(names: true, revisions: false).Count);
        Assert.Equal(22, FootprintPanel.ReturnFields(names: true, revisions: true).Count);
        Assert.Equal(8, FootprintPanel.ReturnFields(names: false, revisions: true).Count);
    }

    // ── Selection by footprint ───────────────────────────────────────────────

    [Fact]
    public void FootprintConditionIsStrictShouldOverAllFourteenNumbers()
    {
        BooleanCondition condition = FootprintPanel.Condition("  R 0603 ");

        Assert.Equal(14, condition.Items.Count);
        Assert.All(condition.Items, item => Assert.Equal(SearchOccur.Should, item.Occur));

        var first = Assert.IsType<StrictCondition>(condition.Items[0].Condition);
        Assert.Equal(SearchFieldNames.FootprintName(1), first.Field);
        Assert.Equal("R 0603", first.Value);

        var last = Assert.IsType<StrictCondition>(condition.Items[13].Condition);
        Assert.Equal(SearchFieldNames.FootprintName(14), last.Field);
    }

    [Theory]
    [InlineData("footprint", true)]
    [InlineData("Footprint", true)]
    [InlineData(" footprintRevision ", true)]
    [InlineData("Value", false)]
    [InlineData("Footprint Name", false)]
    public void VirtualColumnsAreRecognizedByName(string name, bool expected) =>
        Assert.Equal(expected, FootprintPanel.IsColumn(name));

    [Fact]
    public void ParameterFacetsDoNotIncludeFootprintFields()
    {
        // Footprints are not part parameters: they can be neither chosen in columns nor put into filters.
        PanelParameters parameters = PanelParameters.FromFacets(
        [
            new SearchFacet(SearchFieldNames.FootprintName(1), 3, [new("r 0603", 3)], false, null, null),
            new SearchFacet(SearchFieldNames.FootprintRevision(1), 3, [new("pcc-000009-3", 3)], false, null, null),
        ]);

        Assert.Empty(parameters.All);
    }

    [Theory]
    [InlineData("FootprintName1" + P, true)]
    [InlineData("FootprintName14" + P, true)]
    [InlineData("FootprintDescription1" + P, false)]
    [InlineData("FootprintRevisionID1" + P, false)]
    [InlineData("FootprintNameX" + P, false)]
    [InlineData("FootprintName1" + C, false)]
    public void OnlyNameFacetsAreUsedForFootprintSuggestions(string facet, bool expected) =>
        Assert.Equal(expected, FootprintPanel.IsNameFacet(facet));

    // ── Reading a document ───────────────────────────────────────────────────

    [Fact]
    public void SingleFootprintIsShownAsIs()
    {
        FootprintInfo info = FootprintPanel.Read(Document(
            (SearchFieldNames.FootprintName(1), "R 0603"),
            (SearchFieldNames.FootprintRevision(1), "PCC-0009-3")));

        Assert.Equal("R 0603", FootprintPanel.FormatNames(info));
        Assert.Equal("PCC-0009-3", FootprintPanel.FormatRevisions(info));
    }

    [Fact]
    public void SeveralFootprintsAreListedInNumberOrderWithCount()
    {
        // The fields come in arbitrary order; Read returns the order of the numbers.
        FootprintInfo info = FootprintPanel.Read(Document(
            (SearchFieldNames.FootprintName(3), "Juper 400"),
            (SearchFieldNames.FootprintName(1), "Juper 200"),
            (SearchFieldNames.FootprintName(2), "Juper 300"),
            (SearchFieldNames.FootprintRevision(2), "PCC-2-1"),
            (SearchFieldNames.FootprintRevision(1), "PCC-1-1")));

        Assert.Equal("Juper 200, Juper 300, Juper 400 [3]", FootprintPanel.FormatNames(info));
        Assert.Equal("PCC-1-1, PCC-2-1 [2]", FootprintPanel.FormatRevisions(info));
    }

    [Fact]
    public void DocumentWithoutFootprintGivesNothing()
    {
        FootprintInfo info = FootprintPanel.Read(Document((SearchFieldNames.FootprintName(1), string.Empty), ("HRID" + C, "CMP-1")));

        Assert.Empty(info.Names);
        Assert.Null(FootprintPanel.FormatNames(info));
        Assert.Null(FootprintPanel.FormatRevisions(info));
    }

    [Fact]
    public void ResponseWithFootprintFieldsAndFacetIsParsed()
    {
        // The response shape checked on the production server: the fields are returned in ReturnFields, FootprintName1 is a facet.
        string json = $$"""
            {
              "Total": 845, "Success": true,
              "Documents": [
                { "Score": 0, "Fields": [
                    { "Name": "FootprintName1{{P}}", "Value": "R 0603", "FieldType": 3 },
                    { "Name": "FootprintRevisionID1{{P}}", "Value": "PCC-0009-3", "FieldType": 3 },
                    { "Name": "FolderFullPath{{C}}", "Value": "Components\\Resistors\\0603\\", "FieldType": 3 } ] }
              ],
              "FacetedCounters": [
                { "FacetName": "FootprintName1{{P}}", "TotalHitCount": 840,
                  "Counters": [ { "Value": "r 0603", "Count": 840 } ], "SupportRange": false }
              ]
            }
            """;

        SearchResponse response = SearchResponse.Parse(json);

        SearchDocument document = Assert.Single(response.Documents);
        Assert.Equal("R 0603", FootprintPanel.FormatNames(FootprintPanel.Read(document)));
        Assert.Equal("Components\\Resistors\\0603", FootprintPanel.TrimFolder(document.Get(SearchFieldNames.Core("FolderFullPath"))));
        Assert.Equal(840, Assert.Single(response.Facets).TotalHitCount);
    }

    // ── Folder ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Components\\Resistors", "Components\\Resistors\\*")]
    [InlineData("Components\\Resistors\\", "Components\\Resistors\\*")]
    [InlineData("  Components\\Resistors\\  ", "Components\\Resistors\\*")]
    public void FolderConditionIsPrefixWildcardThatIncludesNestedFolders(string path, string expected)
    {
        var condition = Assert.IsType<WildcardCondition>(FootprintPanel.FolderCondition(path));

        Assert.Equal(SearchFieldNames.Core("FolderFullPath"), condition.Field);
        Assert.Equal(expected, condition.Value);
    }

    [Theory]
    [InlineData("Components\\Connectors\\Headers\\", "Components\\Connectors\\Headers")]
    [InlineData("Components", "Components")]
    [InlineData(null, null)]
    public void TrimFolderRemovesTrailingBackslash(string? path, string? expected) =>
        Assert.Equal(expected, FootprintPanel.TrimFolder(path));

    // ── Lifecycle ────────────────────────────────────────────────────────────

    [Fact]
    public void HiddenStatesAreTheOnesNotApplicable()
    {
        var states = new[]
        {
            new ALU_LifeCycleState { GUID = "706004B5-C76E-482E-903B-BA0E4FAE086A", HRID = "Draft", IsApplicable = true },
            new ALU_LifeCycleState { GUID = "C63D42CC-F957-439A-81B0-53EBC4739367", HRID = "Obsolete", IsApplicable = false },
            new ALU_LifeCycleState { GUID = "BD688338-95FF-4FF2-955E-EF609A7DC16D", HRID = "Abandoned", IsApplicable = false },
            new ALU_LifeCycleState { GUID = string.Empty, HRID = "Broken", IsApplicable = false },
        };

        IReadOnlyList<ALU_LifeCycleState> hidden = FootprintPanel.HiddenStates(states);

        Assert.Equal(["Obsolete", "Abandoned"], hidden.Select(state => state.HRID));
    }

    [Fact]
    public void BaseConditionExcludesGivenStatesInLowerCaseLikeThePanelDoes()
    {
        BooleanCondition condition = SearchClient.ComponentsBase(["C63D42CC-F957-439A-81B0-53EBC4739367"]);

        Assert.Equal(5, condition.Items.Count);
        BooleanItem excluded = condition.Items[4];
        Assert.Equal(SearchOccur.MustNot, excluded.Occur);

        var strict = Assert.IsType<StrictCondition>(excluded.Condition);
        Assert.Equal(SearchFieldNames.Core("LifeCycleStateGUID"), strict.Field);
        Assert.Equal("c63d42cc-f957-439a-81b0-53ebc4739367", strict.Value);

        // Without a list the condition is as before: four common conditions.
        Assert.Equal(4, SearchClient.ComponentsBase().Items.Count);
    }

    [Fact]
    public void FolderAndStateConditionsSerializeToSearchRequest()
    {
        BooleanCondition condition = SearchClient.ComponentsBase(["C63D42CC-F957-439A-81B0-53EBC4739367"])
            .With(FootprintPanel.FolderCondition("Components\\Resistors"))
            .With(FootprintPanel.Condition("R 0603"));

        string body = new SearchRequest
        {
            Condition = condition,
            Limit = 5,
            ReturnFields = FootprintPanel.ReturnFields(names: true, revisions: false),
        }.ToJson();

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement request = document.RootElement.GetProperty("request");
        Assert.Equal(14, request.GetProperty("ReturnFields").GetArrayLength());

        JsonElement[] items = request.GetProperty("Condition").GetProperty("Items").EnumerateArray().ToArray();
        Assert.Equal("DtoSearchConditionWildcardQuery", items[5].GetProperty("Item").GetProperty("$type").GetString());
        Assert.Equal("Components\\Resistors\\*", items[5].GetProperty("Item").GetProperty("Term").GetProperty("Value").GetString());
        Assert.Equal(14, items[6].GetProperty("Item").GetProperty("Items").GetArrayLength());
    }
}
