using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Tests;

public sealed class ComponentTableTests
{
    private static readonly string[] Columns = ["hrid", "folder", "comment", "description", "Value", "Tolerance"];

    private static IReadOnlyList<IReadOnlyList<string?>> Rows(params string?[][] rows) =>
        rows.Select(row => (IReadOnlyList<string?>)row).ToList();

    [Fact]
    public void ParseSkipsFolderAndReadsCommentAndDescription()
    {
        var edits = ComponentTable.Parse(
            Columns,
            Rows(["CMP-001", @"Components\X", "10k 0603", "\u0420\u0435\u0437\u0438\u0441\u0442\u043e\u0440", "10k", "1%"]));

        TableEdit edit = Assert.Single(edits);
        Assert.Equal("CMP-001", edit.Component);
        Assert.Equal("10k 0603", edit.Comment);
        Assert.Equal("\u0420\u0435\u0437\u0438\u0441\u0442\u043e\u0440", edit.Description);
        Assert.Equal(["Value", "Tolerance"], edit.Parameters.Keys.OrderByDescending(key => key));
        Assert.DoesNotContain("folder", edit.Parameters.Keys);
        Assert.Empty(edit.DeleteParameters);
    }

    [Fact]
    public void EmptyCellIsEmptyValueByDefault()
    {
        var edit = ComponentTable.Parse(Columns, Rows(["CMP-001", "f", "c", "d", "", null]))[0];

        Assert.Equal(string.Empty, edit.Parameters["Value"]);
        Assert.Equal(string.Empty, edit.Parameters["Tolerance"]);
        Assert.Empty(edit.DeleteParameters);
    }

    [Fact]
    public void EmptyCellDeletesParameterWhenRequested()
    {
        var edit = ComponentTable.Parse(
            Columns, Rows(["CMP-001", "f", "c", "d", "10k", ""]), emptyMeansDelete: true)[0];

        Assert.Equal("10k", edit.Parameters["Value"]);
        Assert.DoesNotContain("Tolerance", edit.Parameters.Keys);
        Assert.Equal(["Tolerance"], edit.DeleteParameters);
    }

    [Fact]
    public void HridColumnIsRequired()
    {
        var error = Assert.Throws<ArgumentException>(
            () => ComponentTable.Parse(["Value"], Rows(["10k"])));

        Assert.Contains("hrid", error.Message);
    }

    [Fact]
    public void HridColumnNameIsCaseInsensitive()
    {
        var edit = ComponentTable.Parse(["HRID", "Value"], Rows(["CMP-002 ", "1k"]))[0];

        Assert.Equal("CMP-002", edit.Component);
        Assert.Null(edit.Comment);
        Assert.Null(edit.Description);
    }

    [Fact]
    public void RowLengthMustMatchColumns()
    {
        var error = Assert.Throws<ArgumentException>(
            () => ComponentTable.Parse(["hrid", "Value"], Rows(["CMP-001"])));

        Assert.Contains("Row 1", error.Message);
    }

    [Fact]
    public void EmptyHridInRowIsRejected()
    {
        var error = Assert.Throws<ArgumentException>(
            () => ComponentTable.Parse(["hrid", "Value"], Rows(["CMP-001", "1k"], ["", "2k"])));

        Assert.Contains("row 2", error.Message);
    }

    [Fact]
    public void BuildProducesFixedColumnsThenUnionOfParameters()
    {
        ComponentRecord Make(string hrid, params (string Name, string Value)[] parameters) => new()
        {
            ItemGuid = Guid.NewGuid().ToString(),
            Hrid = hrid,
            FolderPath = @"Components\X",
            FolderGuid = Guid.NewGuid().ToString(),
            RevisionId = "2",
            Comment = "c",
            Parameters = parameters.ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase),
        };

        var table = ComponentTable.Build(
            [Make("CMP-001", ("Value", "10k")), Make("CMP-002", ("Tolerance", "1%"))]);

        Assert.Equal(["hrid", "folder", "revision", "comment", "description", "Tolerance", "Value"], table.Columns);
        Assert.Equal(table.Columns.Count, table.Rows[0].Count);

        // A parameter the component does not have is null, not an empty string.
        Assert.Null(table.Rows[0][5]);
        Assert.Equal("10k", table.Rows[0][6]);
        Assert.Equal("1%", table.Rows[1][5]);

        var only = ComponentTable.Build([Make("CMP-001", ("Value", "10k"))], ["Value"]);
        Assert.Equal(["hrid", "folder", "revision", "comment", "description", "Value"], only.Columns);
    }
}
