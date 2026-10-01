using System.Text.Json.Nodes;
using AltiumWorkspaceMCP.Vault;
using AltiumWorkspaceMCP.Vault.Rest;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>Protection against duplicates by MPN and LCSC Part#: search conditions, skipping empty ones, parsing the response.</summary>
public sealed class DuplicateFinderTests
{
    private static readonly string Mpn = SearchFieldNames.Parameter("Manufacturer Part Number");
    private static readonly string Lcsc = SearchFieldNames.Parameter("LCSC Part#");
    private static readonly string Hrid = SearchFieldNames.Parameter("ItemHRID");
    private static readonly string Comment = SearchFieldNames.Core("Comment");
    private static readonly string Folder = SearchFieldNames.Core("FolderFullPath");

    private static Dictionary<string, string> Params(params (string Name, string Value)[] items) =>
        items.ToDictionary(item => item.Name, item => item.Value, StringComparer.OrdinalIgnoreCase);

    private static SearchDocument Doc(string hrid, string? mpn = null, string? lcsc = null)
    {
        var fields = new Dictionary<string, string>
        {
            [Hrid] = hrid,
            [Comment] = "name " + hrid,
            [Folder] = @"Components\Test\",
        };

        if (mpn is not null)
        {
            fields[Mpn] = mpn;
        }

        if (lcsc is not null)
        {
            fields[Lcsc] = lcsc;
        }

        return new SearchDocument(0, fields);
    }

    // ── What is searched ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("-", false)]
    [InlineData("--", false)]
    [InlineData("—", false)]
    [InlineData(" – ", false)]
    [InlineData("RC0805FR-0710KL", true)]
    [InlineData("C25804", true)]
    [InlineData("-5V", true)]
    public void OnlyRealValuesAreSearched(string? value, bool expected) =>
        Assert.Equal(expected, DuplicateFinder.IsSearchable(value));

    [Fact]
    public void KeysTakeOnlyMpnAndLcscAndSkipEmptyValues()
    {
        var keys = DuplicateFinder.Keys(Params(
            ("manufacturer part number", " RC0805FR-0710KL "),
            ("LCSC Part#", "-"),
            ("Value", "10k")));

        DuplicateKey key = Assert.Single(keys);
        Assert.Equal("Manufacturer Part Number", key.Parameter);
        Assert.Equal("RC0805FR-0710KL", key.Value);
    }

    [Fact]
    public void KeysAreEmptyWhenNothingToSearch() =>
        Assert.Empty(DuplicateFinder.Keys(Params(("Manufacturer Part Number", ""), ("LCSC Part#", "—"))));

    [Fact]
    public void CopyInheritsSourceValueUnlessOverridden()
    {
        var source = Params(("Manufacturer Part Number", "RC0805FR-0710KL"), ("LCSC Part#", "C17414"));

        var inherited = DuplicateFinder.Keys(DuplicateFinder.Effective(source, Params(("Value", "22k"))));
        Assert.Equal(2, inherited.Count);

        var changed = DuplicateFinder.Keys(DuplicateFinder.Effective(source, Params(("manufacturer part number", "NEW-1"))));
        Assert.Contains(changed, key => key is { Parameter: "Manufacturer Part Number", Value: "NEW-1" });
        Assert.Contains(changed, key => key.Parameter == "LCSC Part#");

        var cleared = DuplicateFinder.Keys(DuplicateFinder.Effective(source, Params(("Manufacturer Part Number", ""))));
        Assert.DoesNotContain(cleared, key => key.Parameter == "Manufacturer Part Number");
    }

    // ── The search condition ─────────────────────────────────────────────────

    [Fact]
    public void ConditionIsEmptyWithoutKeys() =>
        Assert.Empty(DuplicateFinder.BuildCondition([]).Items);

    [Fact]
    public void ConditionHasShouldGroupOfStrictConditionsPerDistinctPair()
    {
        var condition = DuplicateFinder.BuildCondition(
        [
            new DuplicateKey("Manufacturer Part Number", "ABC-1"),
            new DuplicateKey("Manufacturer Part Number", "abc-1"), // the same key, case-insensitive
            new DuplicateKey("LCSC Part#", "C123"),
        ]);

        // The last element is a Should group with one strict condition per pair.
        BooleanItem last = condition.Items[^1];
        Assert.Equal(SearchOccur.Must, last.Occur);
        var group = Assert.IsType<BooleanCondition>(last.Condition);
        Assert.Equal(2, group.Items.Count);
        Assert.All(group.Items, item => Assert.Equal(SearchOccur.Should, item.Occur));

        var first = Assert.IsType<StrictCondition>(group.Items[0].Condition);
        Assert.Equal(Mpn, first.Field);
        Assert.Equal("ABC-1", first.Value);
        Assert.Equal(Lcsc, Assert.IsType<StrictCondition>(group.Items[1].Condition).Field);
    }

    [Fact]
    public void ConditionKeepsPanelBaseAndHiddenStates()
    {
        var condition = DuplicateFinder.BuildCondition([new DuplicateKey("LCSC Part#", "C1")], ["AAAA-BBBB"]);

        JsonObject json = condition.ToJson();
        string text = json.ToJsonString();
        Assert.Contains("aaaa-bbbb", text);
        Assert.Contains("Component", text);
    }

    [Fact]
    public void ReturnFieldsCarryHridCommentFolderAndBothParameters()
    {
        var fields = DuplicateFinder.ReturnFields();
        Assert.Contains(Hrid, fields);
        Assert.Contains(Comment, fields);
        Assert.Contains(Folder, fields);
        Assert.Contains(Mpn, fields);
        Assert.Contains(Lcsc, fields);
    }

    // ── Parsing the response ─────────────────────────────────────────────────

    [Fact]
    public void MatchIsCaseInsensitiveAndGroupsByPair()
    {
        var keys = new[]
        {
            new DuplicateKey("Manufacturer Part Number", "RC0805FR-0710KL"),
            new DuplicateKey("LCSC Part#", "C17414"),
        };

        var hits = DuplicateFinder.Match(keys,
        [
            Doc("CMP-000-00040", mpn: "rc0805fr-0710kl", lcsc: "C17414"),
            Doc("CMP-000-00041", mpn: "OTHER"),
        ]);

        Assert.Equal(2, hits.Count);
        DuplicateHit mpn = hits.Single(hit => hit.Parameter == "Manufacturer Part Number");
        Assert.Equal(["CMP-000-00040"], mpn.Existing.Select(part => part.Hrid));
        Assert.Equal(@"Components\Test", mpn.Existing[0].Folder);
    }

    [Fact]
    public void MatchSkipsExcludedPartsAndReportsNothingWhenNoneLeft()
    {
        var keys = new[] { new DuplicateKey("Manufacturer Part Number", "X1") };
        var documents = new[] { Doc("CMP-1", mpn: "X1") };

        Assert.Empty(DuplicateFinder.Match(keys, documents, exclude: ["cmp-1"]));
        Assert.Single(DuplicateFinder.Match(keys, documents));
    }

    [Fact]
    public void MatchDoesNotConfuseParameters()
    {
        // In the document the value is in LCSC Part#, but it was searched as MPN — this does not count as a match.
        var hits = DuplicateFinder.Match([new DuplicateKey("Manufacturer Part Number", "C1")], [Doc("CMP-2", lcsc: "C1")]);
        Assert.Empty(hits);
    }

    // ── Duplicates inside one call ──────────────────────────────────

    [Fact]
    public void WithinCallFindsSameValueInTwoRows()
    {
        var rows = new[]
        {
            new CallRow("1", [new DuplicateKey("Manufacturer Part Number", "NEW-1")]),
            new CallRow("2", [new DuplicateKey("Manufacturer Part Number", "NEW-1")]),
        };

        WithinCallHit hit = Assert.Single(DuplicateFinder.WithinCall(rows));
        Assert.Equal("Manufacturer Part Number", hit.Parameter);
        Assert.Equal("NEW-1", hit.Value);
        Assert.Equal(["1", "2"], hit.RowIds);
    }

    [Fact]
    public void WithinCallIsCaseAndSpaceInsensitive()
    {
        var rows = new[]
        {
            new CallRow("1", [new DuplicateKey("Manufacturer Part Number", "new-1")]),
            new CallRow("2", [new DuplicateKey("manufacturer part number", "NEW-1")]),
        };

        Assert.Single(DuplicateFinder.WithinCall(rows));
    }

    [Fact]
    public void WithinCallIgnoresBlanksAlreadyFilteredByKeys()
    {
        // Keys() does not return empty values and dashes — WithinCall will not see them either.
        var rows = new[]
        {
            new CallRow("1", DuplicateFinder.Keys(Params(("Manufacturer Part Number", "-")))),
            new CallRow("2", DuplicateFinder.Keys(Params(("Manufacturer Part Number", "—")))),
        };

        Assert.Empty(DuplicateFinder.WithinCall(rows));
    }

    [Fact]
    public void WithinCallDoesNotCountOneRowTwice()
    {
        // The same text in MPN and LCSC Part# of one row is not a match of the row with itself.
        var rows = new[]
        {
            new CallRow("1", [new DuplicateKey("Manufacturer Part Number", "X1"), new DuplicateKey("LCSC Part#", "X1")]),
        };

        Assert.Empty(DuplicateFinder.WithinCall(rows));
    }

    [Fact]
    public void WithinCallFindsInheritedSourceValueSharedByTwoCopies()
    {
        // Both copies inherit the sample's MPN without changing it — this is the main case of duplicates inside one call.
        var source = Params(("Manufacturer Part Number", "RC0805FR-0710KL"));

        var rows = new[]
        {
            new CallRow("1", DuplicateFinder.Keys(DuplicateFinder.Effective(source, Params(("Value", "10k"))))),
            new CallRow("2", DuplicateFinder.Keys(DuplicateFinder.Effective(source, Params(("Value", "22k"))))),
        };

        WithinCallHit hit = Assert.Single(DuplicateFinder.WithinCall(rows));
        Assert.Equal("RC0805FR-0710KL", hit.Value);
        Assert.Equal(["1", "2"], hit.RowIds);
    }

    [Fact]
    public void WithinCallIgnoresValueUsedOnlyOnce()
    {
        var rows = new[]
        {
            new CallRow("1", [new DuplicateKey("Manufacturer Part Number", "A1")]),
            new CallRow("2", [new DuplicateKey("Manufacturer Part Number", "A2")]),
        };

        Assert.Empty(DuplicateFinder.WithinCall(rows));
    }
}
