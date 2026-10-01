using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>Matching folder addresses on a synthetic tree.</summary>
public sealed class FolderResolverTests
{
    private const string Resistors = @"Components\Passive Components\Resistors";
    private const string SymbolsResistors = @"Symbols\Resistors";

    private static readonly FolderNode[] Tree = Build(
        "Components",
        @"Components\Passive Components",
        Resistors,
        Resistors + @"\Datasheets",
        @"Components\Passive Components\Capacitors",
        @"Components\Integrated Circuits",
        @"Components\Integrated Circuits\Logic Gates",
        @"Components\Integrated Circuits\Microcontrollers",
        "Symbols",
        SymbolsResistors);

    private static FolderNode[] Build(params string[] paths) =>
        paths.Select((path, index) => new FolderNode(
            Guid: $"00000000-0000-0000-0000-{index:D12}",
            Name: path[(path.LastIndexOf('\\') + 1)..],
            Path: path,
            Description: null,
            ParentGuid: null,
            FolderTypeGuid: null)).ToArray();

    private static FolderMatch Resolve(string request, FolderMatchMode mode = FolderMatchMode.Lenient) =>
        FolderResolver.Resolve(Tree, request, mode);

    [Theory]
    [InlineData(Resistors, "path")]
    [InlineData(@"components\passive components\RESISTORS", "path")]
    [InlineData("Components/Passive Components/Resistors", "path")]
    [InlineData(@"  \Components\\Passive   Components\Resistors\ ", "path")]
    [InlineData(@"Passive Components\Resistors", "suffix")]
    [InlineData(@"passive components/resistors/", "suffix")]
    [InlineData("Capacitors", "name")]
    [InlineData("logic gates", "name")]
    public void ExactAddressesInBothModes(string request, string how)
    {
        foreach (FolderMatchMode mode in Enum.GetValues<FolderMatchMode>())
        {
            FolderMatch match = Resolve(request, mode);

            Assert.Equal(how, match.How);
            Assert.EndsWith(FolderResolver.Segments(request)[^1], match.Folder.Path, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void GuidIsFoundInAnyCase()
    {
        string guid = Tree[3].Guid.ToUpperInvariant();

        FolderMatch match = Resolve(guid.ToLowerInvariant(), FolderMatchMode.Strict);

        Assert.Equal("guid", match.How);
        Assert.Equal(Tree[3].Path, match.Folder.Path);
        Assert.True(match.IsExact);
        Assert.Null(match.Describe(guid));
    }

    [Fact]
    public void JournalCaseIsResolvedLeniently()
    {
        FolderMatch resistors = Resolve(@"Passive\Resistors");
        Assert.Equal(Resistors, resistors.Folder.Path);
        Assert.Equal("fuzzy", resistors.How);
        Assert.False(resistors.IsExact);
        Assert.NotNull(resistors.Describe(@"Passive\Resistors"));

        FolderMatch logic = Resolve(@"Integrated Circuits\Logic");
        Assert.Equal(@"Components\Integrated Circuits\Logic Gates", logic.Folder.Path);
    }

    [Fact]
    public void JournalCaseIsRefusedStrictlyWithTheRightCandidate()
    {
        var error = Assert.Throws<FolderNotFoundException>(
            () => Resolve(@"Passive\Resistors", FolderMatchMode.Strict));

        Assert.Contains(Resistors, error.Message);
        Assert.Contains("vault_folders nameContains=\"Resistors\"", error.Message);
        Assert.StartsWith("Folder 'Passive\\Resistors' not found", error.Message);

        // The best candidate goes first.
        Assert.True(error.Message.IndexOf(Resistors, StringComparison.Ordinal)
            < error.Message.IndexOf(SymbolsResistors, StringComparison.Ordinal));
    }

    [Fact]
    public void SameNameInTwoBranchesIsAmbiguousInBothModes()
    {
        foreach (FolderMatchMode mode in Enum.GetValues<FolderMatchMode>())
        {
            var error = Assert.Throws<InvalidOperationException>(() => Resolve("Resistors", mode));

            Assert.Contains(Resistors, error.Message);
            Assert.Contains(SymbolsResistors, error.Message);
        }
    }

    [Fact]
    public void LowercaseSingularTieIsNotGuessed()
    {
        // "resistor" fits both Resistors folders equally — choosing is not allowed.
        var error = Assert.Throws<FolderNotFoundException>(() => Resolve("resistor"));

        Assert.Contains(Resistors, error.Message);
        Assert.Contains(SymbolsResistors, error.Message);
    }

    [Fact]
    public void SingularAndLowercaseResolveWhenOnlyOneBranchFits()
    {
        FolderMatch match = Resolve("passive components\\resistor");

        Assert.Equal(Resistors, match.Folder.Path);
        Assert.Equal("fuzzy", match.How);

        Assert.Equal(@"Components\Integrated Circuits\Logic Gates", Resolve("Integrated Circuits\\Logic Gate").Folder.Path);
    }

    [Fact]
    public void SystemFolderIsNeverPickedLeniently()
    {
        Assert.Throws<FolderNotFoundException>(() => Resolve("datasheet"));
        Assert.Throws<FolderNotFoundException>(() => Resolve(@"Passive\Datasheet"));

        // An explicitly named system folder is found.
        FolderMatch explicitMatch = Resolve("Datasheets");
        Assert.Equal(Resistors + @"\Datasheets", explicitMatch.Folder.Path);
        Assert.Equal("name", explicitMatch.How);

        Assert.Equal(
            Resistors + @"\Datasheets",
            Resolve(Resistors + @"\Datasheets", FolderMatchMode.Strict).Folder.Path);
        Assert.Equal("suffix", Resolve(@"Resistors\Datasheets").How);
    }

    [Fact]
    public void UnknownAddressListsTopLevelFolders()
    {
        var error = Assert.Throws<FolderNotFoundException>(() => Resolve("Quantum Flux"));

        Assert.Contains("no similar ones", error.Message);
        Assert.Contains("Components", error.Message);
        Assert.Contains(@"Components\Passive Components", error.Message);
        Assert.DoesNotContain(Resistors, error.Message);
        Assert.DoesNotContain("Datasheets", error.Message);
    }

    [Fact]
    public void AmbiguousListIsCappedAtTen()
    {
        var many = Build(Enumerable.Range(0, 15).Select(number => $@"Branch{number}\Common").ToArray());

        var error = Assert.Throws<InvalidOperationException>(
            () => FolderResolver.Resolve(many, "Common", FolderMatchMode.Strict));

        Assert.Contains("(15)", error.Message);
        Assert.Equal(10, CountBranches(error.Message));
        Assert.Contains("; …", error.Message);
    }

    private static int CountBranches(string text) =>
        text.Split("Branch", StringSplitOptions.None).Length - 1;

    [Fact]
    public void EmptyAddressIsRejected() =>
        Assert.Throws<ArgumentException>(() => Resolve(" \\ / "));

    [Theory]
    [InlineData("Passive Components", "Passive")]
    [InlineData("Integrated Circuits", "integrated")]
    public void SingleSegmentWordMatchIsStillFuzzyOnlyWhenUnique(string exact, string loose)
    {
        Assert.Equal("name", Resolve(exact).How);

        FolderMatch match = Resolve(loose);
        Assert.Equal("fuzzy", match.How);
        Assert.EndsWith(exact, match.Folder.Path, StringComparison.Ordinal);
    }
}
