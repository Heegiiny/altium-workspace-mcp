using AltiumWorkspaceMCP.Tools;
using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>A compact "part → footprint" list: the column set and the gain in the number of rows.</summary>
public sealed class ComponentPanelCompactTests
{
    [Fact]
    public void CompactKeepsOnlyHrid() =>
        Assert.Equal(["hrid"], ComponentPanelService.FixedColumns(compact: true));

    [Fact]
    public void NonCompactKeepsPriorFixedColumns() =>
        Assert.Equal(ComponentTable.FixedColumns, ComponentPanelService.FixedColumns(compact: false));

    /// <summary>
    /// A <c>list</c> row without <c>compact</c> — hrid, folder, revision, comment,
    /// description, footprint, about 155 characters; with <c>compact</c> — hrid and footprint, about 40 characters.
    /// Builds representative rows of both kinds and shows that within the 20 000-character response budget
    /// (as for the other <c>vault_components</c> actions) more rows fit with compact.
    /// </summary>
    [Fact]
    public void CompactFitsMoreRowsInResponseBudgetThanFullColumns()
    {
        const int maxChars = 20000;
        const int rowCount = 1000;

        var fullRows = Enumerable.Range(1, rowCount)
            .Select(i => (IReadOnlyList<string?>)new List<string?>
            {
                $"CMP-000-{i:0000}",
                @"Passive Components\Resistors\SMD\0603",
                "3",
                "10 kOhm 0603 Thick Film Resistor 1%",
                "Chip resistor, thick film, 0603 package, 1% tolerance",
                "R 0603",
            })
            .ToList();

        var compactRows = Enumerable.Range(1, rowCount)
            .Select(i => (IReadOnlyList<string?>)new List<string?> { $"CMP-000-{i:0000}", "R 0603" })
            .ToList();

        var fullColumns = ComponentPanelService.FixedColumns(compact: false).Append("footprint").ToList();
        var compactColumns = ComponentPanelService.FixedColumns(compact: true).Append("footprint").ToList();

        int fullReserved = ResponseBudget.SizeOf(fullColumns) + 1500;
        int compactReserved = ResponseBudget.SizeOf(compactColumns) + 1500;

        var fullFit = new ResponseBudget(maxChars, fullReserved).TakeFitting(fullRows);
        var compactFit = new ResponseBudget(maxChars, compactReserved).TakeFitting(compactRows);

        Assert.True(
            compactFit.Count > fullFit.Count,
            $"compact={compactFit.Count} rows must be more than without compact={fullFit.Count} rows.");

        // The order of magnitude measured on a real vault: without compact ~120 rows per call (a row of ~155 characters);
        // compact is noticeably shorter, so several times more rows fit.
        Assert.InRange(fullFit.Count, 80, 160);
        Assert.True(compactFit.Count >= fullFit.Count * 2, $"compact={compactFit.Count} must be at least twice full={fullFit.Count}.");
    }
}
