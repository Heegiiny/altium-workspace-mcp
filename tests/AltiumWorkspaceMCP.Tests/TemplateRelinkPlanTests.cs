using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>Counting lagging parts and parsing relink — pure logic without a server.</summary>
public sealed class TemplateRelinkPlanTests
{
    private static ComponentRecord Component(string hrid, string guid) => new()
    {
        ItemGuid = guid,
        Hrid = hrid,
        FolderPath = "Components\\Test",
        FolderGuid = "F",
    };

    private static readonly IReadOnlyList<ComponentRecord> Users =
    [
        Component("CMP-001", "AAAA"),
        Component("CMP-002", "BBBB"),
        Component("CMP-003", "CCCC"),
    ];

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("none", false)]
    [InlineData(" NONE ", false)]
    [InlineData("all", true)]
    [InlineData("ALL", true)]
    public void ParseRelink_AcceptsNoneAndAll(string? value, bool expected) =>
        Assert.Equal(expected, TemplateRelinkPlan.ParseRelink(value));

    [Theory]
    [InlineData("source")]
    [InlineData("some")]
    public void ParseRelink_RejectsOthers(string value) =>
        Assert.Throws<ArgumentException>(() => TemplateRelinkPlan.ParseRelink(value));

    [Fact]
    public void Select_EmptyRequest_TakesAllLaggards()
    {
        var (selected, absent) = TemplateRelinkPlan.Select(Users, []);

        Assert.Equal(3, selected.Count);
        Assert.Empty(absent);
    }

    [Fact]
    public void Select_BlankNamesAreIgnored()
    {
        var (selected, absent) = TemplateRelinkPlan.Select(Users, ["", "  "]);

        Assert.Equal(3, selected.Count);
        Assert.Empty(absent);
    }

    [Fact]
    public void Select_ByHridOrGuid_CaseInsensitive()
    {
        var (selected, absent) = TemplateRelinkPlan.Select(Users, ["cmp-001", "bbbb"]);

        Assert.Equal(["CMP-001", "CMP-002"], selected.Select(user => user.Hrid));
        Assert.Empty(absent);
    }

    [Fact]
    public void Select_NamedButNotLagging_IsReportedAbsent()
    {
        var (selected, absent) = TemplateRelinkPlan.Select(Users, ["CMP-002", "CMP-999"]);

        Assert.Equal(["CMP-002"], selected.Select(user => user.Hrid));
        Assert.Equal(["CMP-999"], absent);
    }

    [Fact]
    public void Remaining_WithoutRequest_CountsAllStillLagging() =>
        Assert.Equal(3, TemplateRelinkPlan.Remaining(["CMP-001", "CMP-002", "CMP-003"], []));

    [Fact]
    public void Remaining_WithRequest_CountsOnlyNamed() =>
        Assert.Equal(1, TemplateRelinkPlan.Remaining(["CMP-001", "CMP-002", "CMP-003"], ["cmp-002", "CMP-777"]));

    [Fact]
    public void RelinkCall_IsReadyToPaste() =>
        Assert.Equal(
            "vault_template {\"action\":\"relink\",\"template\":\"CMPT-0013\"}",
            TemplateRelinkPlan.RelinkCall("CMPT-0013"));

    [Fact]
    public void DescribeLaggards_ShowsCountSampleAndCall()
    {
        var many = Enumerable.Range(1, 45).Select(number => Component($"CMP-{number:000}", $"G{number}")).ToList();

        string json = System.Text.Json.JsonSerializer.Serialize(TemplateRelinkPlan.DescribeLaggards("CMPT-0013", many));
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(45, root.GetProperty("count").GetInt32());
        Assert.Equal(TemplateRelinkPlan.SampleSize, root.GetProperty("components").GetArrayLength());
        Assert.Equal(35, root.GetProperty("omitted").GetInt32());
        Assert.Contains("CMPT-0013", root.GetProperty("relinkCall").GetString());
        Assert.Contains("role:template", root.GetProperty("hint").GetString());
    }

    [Fact]
    public void DescribeLaggards_NoLaggards_HasNoCallAndNoHint()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(TemplateRelinkPlan.DescribeLaggards("CMPT-0013", []));
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(0, root.GetProperty("count").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("relinkCall").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("hint").ValueKind);
    }
}
