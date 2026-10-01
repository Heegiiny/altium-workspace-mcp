using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>The Datasheets system folder: the Altium reference and the comparison with it.</summary>
public sealed class SystemFolderTests
{
    private static FolderNode Node(string type, int attributes) =>
        new("g", "Datasheets", @"Components\X\Datasheets", null, "p", type, attributes);

    [Fact]
    public void EtalonConstantsMatchAltiumCapture()
    {
        // As Altium Designer creates it: HRID, type, scheme, Attributes=1.
        Assert.Equal("Datasheets", FolderService.DatasheetFolderName);
        Assert.Equal("3893BDB4-A89C-477A-B5BB-D4D52E424DF1", FolderService.DatasheetFolderTypeGuid);
        Assert.Equal("$CONTENT_TYPE_CODE-001-{A00}", FolderService.DatasheetNamingScheme);
        Assert.Equal(1, FolderNode.SystemAttribute);
    }

    [Fact]
    public void FolderLikeAltiumsHasNoDifferences() =>
        Assert.Empty(FolderService.DescribeDifferences(
            Node(FolderService.DatasheetFolderTypeGuid.ToLowerInvariant(), 1), "$CONTENT_TYPE_CODE-001-{A0000}"));

    [Fact]
    public void FolderOfEarlierServerDiffersInAllThree()
    {
        var differences = FolderService.DescribeDifferences(Node("89B6B381-D64E-456E-BF2A-E08CBB186A84", 0), null);

        Assert.Equal(3, differences.Count);
        Assert.Contains(differences, text => text.StartsWith("folder type", StringComparison.Ordinal));
        Assert.Contains(differences, text => text.Contains("Attributes = 0", StringComparison.Ordinal));
        Assert.Contains(differences, text => text.Contains("naming scheme", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("$CONTENT_TYPE_CODE-001-{A00}", "$CONTENT_TYPE_CODE-001-{A0000}", true)]
    [InlineData("$CONTENT_TYPE_CODE-001-{A0000}", "$CONTENT_TYPE_CODE-001-{A00}", true)]
    [InlineData("CMP-016-{00000}", "CMP-016-{00000}", true)]
    [InlineData("CMP-016-{00000}", "CMP-017-{00000}", false)]
    [InlineData(null, "$CONTENT_TYPE_CODE-001-{A00}", false)]
    public void SchemesEqualUpToCounterWidth(string? actual, string expected, bool same) =>
        Assert.Equal(same, FolderService.SameNamingScheme(actual, expected));

    [Fact]
    public void SystemBitMakesFolderSystemAndHiddenFromLenientSearch()
    {
        var real = new FolderNode("1", "Docs", @"C\Docs", null, null, null, Attributes: 1);
        var plain = new FolderNode("2", "Docs2", @"C\Docs2", null, null, null);

        Assert.True(real.IsSystem);
        Assert.False(plain.IsSystem);
        Assert.True(FolderResolver.IsSystem(real));

        // An older Datasheets folder without the bit is also considered a system one — by name.
        Assert.True(FolderResolver.IsSystem(Node("t", 0)));
    }
}
