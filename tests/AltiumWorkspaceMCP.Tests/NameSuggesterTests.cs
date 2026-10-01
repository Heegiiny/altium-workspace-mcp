using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>Typo hints.</summary>
public sealed class NameSuggesterTests
{
    private static readonly string[] Names =
    [
        "Resistance", "Resistance Tolerance", "Power", "Tolerance", "Case/Package", "Capacitance",
        "Manufacturer", "Manufacturer Part Number", "LCSC Part#", "Value", "Voltage Rating",
    ];

    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("Resistance", "resistance", 0)]
    [InlineData("Resistanse", "Resistance", 1)]
    [InlineData("", "abc", 3)]
    public void LevenshteinIgnoresCase(string first, string second, int expected) =>
        Assert.Equal(expected, NameSuggester.Distance(first, second));

    [Fact]
    public void TypoIsFixedByTheClosestName()
    {
        var near = NameSuggester.Nearest("Resistanse", Names);

        Assert.Equal("Resistance", near[0]);
        Assert.True(near.Count <= 5);
    }

    [Fact]
    public void ContainingNamesComeFirstAndCaseIsIgnored()
    {
        var near = NameSuggester.Nearest("manufacturer part", Names);

        Assert.Equal("Manufacturer Part Number", near[0]);
    }

    [Fact]
    public void FarNamesAreNotSuggested() =>
        Assert.Empty(NameSuggester.Nearest("Zzzzzzzz", Names));

    [Fact]
    public void TheNameItselfAndDuplicatesAreSkipped()
    {
        var near = NameSuggester.Nearest("Power", ["power", "Power", "Powers", "POWERS"]);

        Assert.Equal(["Powers"], near);
    }

    [Fact]
    public void AtMostFiveSuggestions()
    {
        var many = Enumerable.Range(0, 30).Select(number => $"Param{number}").ToList();

        Assert.Equal(5, NameSuggester.Nearest("Param", many).Count);
    }

    [Theory]
    [InlineData("CMP-000-0946", true, "CMP-000-")]
    [InlineData("cmp-016-00021", true, "cmp-016-")]
    [InlineData("SYM-000-0076", true, "SYM-000-")]
    [InlineData("CMP-0073", false, null)]
    [InlineData("FP-RMCF2512", false, null)]
    [InlineData("resistor", false, null)]
    public void IdentifierRecognition(string value, bool looks, string? prefix)
    {
        Assert.Equal(looks, NameSuggester.LooksLikeIdentifier(value));
        Assert.Equal(prefix, NameSuggester.IdentifierPrefix(value));
    }

    [Fact]
    public void NeighboursAreClosestByNumberSameFamilyOnly()
    {
        string[] existing =
        [
            "CMP-000-0940", "CMP-000-0941", "CMP-000-0945", "CMP-000-0950", "CMP-000-0960", "CMP-000-1200",
            "CMP-999-0946", "CMP-000-0946",
        ];

        var near = NameSuggester.NearestByNumber("CMP-000-0947", existing, max: 3);

        // 0946 (1), 0945 (2), 0950 (3): the nearest by number, in number order; another family is not taken.
        Assert.Equal(["CMP-000-0945", "CMP-000-0946", "CMP-000-0950"], near);
    }

    [Fact]
    public void NeighboursOfNonIdentifierAreEmpty() =>
        Assert.Empty(NameSuggester.NearestByNumber("resistor", ["CMP-000-0001"]));

    [Fact]
    public void MissingNumberAtTheEndOfFamilyGivesLargestOnes()
    {
        var near = NameSuggester.NearestByNumber("CMP-000-99999", ["CMP-000-0001", "CMP-000-0500", "CMP-000-1200"], 2);

        Assert.Equal(["CMP-000-0500", "CMP-000-1200"], near);
    }
}
