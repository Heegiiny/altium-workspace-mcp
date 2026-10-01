using AltiumWorkspaceMCP.Soap.Vault;
using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>
/// Several footprints on a part: link assembly, numbering, set check.
/// The format reference — part CMP-000-0002 rev. 23, released from Altium: the primary "PCBLIB"
/// with FootprintIndex 0, the additional "PCBLIB 1", "PCBLIB 2" with indexes 1 and 2.
/// </summary>
public sealed class FootprintSetTests
{
    private const string Parent = "AAAAAAAA-0000-0000-0000-000000000001";
    private const string NewParent = "AAAAAAAA-0000-0000-0000-000000000002";

    // Footprint revisions: Normal / Least / Most from the reference part.
    private const string Normal = "11111111-0000-0000-0000-000000000527";
    private const string Least = "11111111-0000-0000-0000-000000000528";
    private const string Most = "11111111-0000-0000-0000-000000000014";
    private const string Extra = "11111111-0000-0000-0000-000000000099";
    private const string SymbolRevision = "22222222-0000-0000-0000-000000000001";

    // Link data — letter for letter, as Altium writes them.
    private const string MainData = "{\"Footprint\":{\"FootprintIndex\":0,\"IsDefaultFootprint\":true}}";
    private const string FirstExtraData = "{\"Footprint\":{\"FootprintIndex\":1,\"IsDefaultFootprint\":false}}";
    private const string SecondExtraData = "{\"Footprint\":{\"FootprintIndex\":2,\"IsDefaultFootprint\":false}}";

    private static ALU_ItemRevisionLink Link(string role, string child, string? data = null, string guid = "") => new()
    {
        GUID = guid,
        HRID = role,
        ParentItemRevisionGUID = Parent,
        ChildItemRevisionGUID = child,
        Data = data,
    };

    private static ALU_ItemRevisionLink Symbol() => Link(LinkRole.Symbol, SymbolRevision, guid: "S");

    /// <summary>A part with three footprints, as Altium releases it.</summary>
    private static List<ALU_ItemRevisionLink> ThreeFootprints() =>
    [
        Symbol(),
        Link("PCBLIB", Normal, MainData, "L0"),
        Link("PCBLIB 1", Least, FirstExtraData, "L1"),
        Link("PCBLIB 2", Most, SecondExtraData, "L2"),
    ];

    private static ComponentChange Set(params string[] targets) => new()
    {
        Links = [new LinkAssignment(LinkRole.Footprints, null, targets)],
    };

    private static List<(string Role, string Child, string Data)> Footprints(IEnumerable<ALU_ItemRevisionLink> links) =>
        FootprintLinks.Ordered(links).Select(link => (link.HRID!, link.ChildItemRevisionGUID!, link.Data!)).ToList();

    // ---------- roles ----------

    [Theory]
    [InlineData("PCBLIB", true, 0)]
    [InlineData("pcblib", true, 0)]
    [InlineData("PCBLIB 1", true, 1)]
    [InlineData("PCBLIB 12", true, 12)]
    [InlineData("pcblib 2", true, 2)]
    [InlineData("PCBLIB 0", false, null)]
    [InlineData("PCBLIB 1a", false, null)]
    [InlineData("PCBLIB -1", false, null)]
    [InlineData("PCBLIB  1", false, null)]
    [InlineData("PCBLIB1", false, null)]
    [InlineData("PCBLIBS", false, null)]
    [InlineData("SCHLIB", false, null)]
    [InlineData("", false, null)]
    [InlineData(null, false, null)]
    public void IsFootprintRecognizesMainAndNumberedRoles(string? hrid, bool expected, int? number)
    {
        Assert.Equal(expected, LinkRole.IsFootprint(hrid));
        Assert.Equal(number, LinkRole.FootprintNumber(hrid));
    }

    [Fact]
    public void FootprintRoleBuildsAltiumNames()
    {
        Assert.Equal("PCBLIB", LinkRole.FootprintRole(0));
        Assert.Equal("PCBLIB 1", LinkRole.FootprintRole(1));
        Assert.Equal("PCBLIB 2", LinkRole.FootprintRole(2));
    }

    [Fact]
    public void DescribeNamesAdditionalFootprints()
    {
        Assert.Equal("footprint", LinkRole.Describe("PCBLIB"));
        Assert.Equal("additional footprint 1", LinkRole.Describe("PCBLIB 1"));
        Assert.Equal("additional footprint 2", LinkRole.Describe("pcblib 2"));
        Assert.Equal("footprints", LinkRole.Describe(LinkRole.Footprints));
        Assert.Equal("symbol", LinkRole.Describe("SCHLIB"));
        Assert.Equal("no role", LinkRole.Describe(""));
    }

    [Theory]
    [InlineData("footprints")]
    [InlineData("Footprints")]
    [InlineData("pcblibs")]
    public void NormalizeRecognizesFootprintSet(string role) =>
        Assert.Equal(LinkRole.Footprints, LinkRole.Normalize(role));

    [Theory]
    [InlineData("symbol", "SCHLIB")]
    [InlineData("Footprint", "PCBLIB")]
    [InlineData("PCBLIB 2", "PCBLIB 2")]
    [InlineData("template", "ComponentTemplate")]
    [InlineData("datasheet", "DATASHEET")]
    [InlineData("simulation", "SIM")]
    public void ParseAcceptsEnglishRoles(string role, string expected) =>
        Assert.Equal(expected, LinkRole.Parse(role));

    [Theory]
    [InlineData("")]
    [InlineData("model")]
    [InlineData("\u0441\u0438\u043c\u0432\u043e\u043b")]
    public void ParseRefusesUnknownRoleAndListsAllowedOnes(string role)
    {
        var error = Assert.Throws<ArgumentException>(() => LinkRole.Parse(role));
        Assert.Contains(LinkRole.AllowedOnInput, error.Message);
    }

    [Fact]
    public void NumberedFootprintIsKnownRole()
    {
        // Otherwise "PCBLIB 1" would be taken for a link without a role and "restored" to "PCBLIB".
        Assert.True(LinkNormalizer.IsKnownRole("PCBLIB"));
        Assert.True(LinkNormalizer.IsKnownRole("PCBLIB 1"));
        Assert.True(LinkNormalizer.IsKnownRole("SCHLIB"));
        Assert.False(LinkNormalizer.IsKnownRole(""));
        Assert.False(LinkNormalizer.IsKnownRole(Guid.NewGuid().ToString()));
    }

    // ---------- set assembly: 1, 2 and 3 footprints, letter for letter ----------

    [Fact]
    public void SetOfOneWritesOnlyMainFootprint()
    {
        var (links, changed) = RevisionService.BuildLinks([Symbol()], Set(Normal), NewParent);

        var footprint = Assert.Single(links, link => LinkRole.IsFootprint(link.HRID));
        Assert.Equal("PCBLIB", footprint.HRID);
        Assert.Equal(Normal, footprint.ChildItemRevisionGUID);
        Assert.Equal(MainData, footprint.Data);
        Assert.Equal(NewParent, footprint.ParentItemRevisionGUID);
        Assert.Equal(2, links.Count);
        Assert.Single(changed);
    }

    [Fact]
    public void SetOfTwoWritesMainAndFirstExtra()
    {
        var (links, _) = RevisionService.BuildLinks([Symbol()], Set(Normal, Least), NewParent);

        Assert.Equal(
            [("PCBLIB", Normal, MainData), ("PCBLIB 1", Least, FirstExtraData)],
            Footprints(links));
    }

    [Fact]
    public void SetOfThreeMatchesAltiumTableLetterForLetter()
    {
        var (links, _) = RevisionService.BuildLinks([Symbol()], Set(Normal, Least, Most), NewParent);

        Assert.Equal(
            [
                ("PCBLIB", Normal, "{\"Footprint\":{\"FootprintIndex\":0,\"IsDefaultFootprint\":true}}"),
                ("PCBLIB 1", Least, "{\"Footprint\":{\"FootprintIndex\":1,\"IsDefaultFootprint\":false}}"),
                ("PCBLIB 2", Most, "{\"Footprint\":{\"FootprintIndex\":2,\"IsDefaultFootprint\":false}}"),
            ],
            Footprints(links));

        // The symbol is untouched.
        Assert.Contains(links, link => link.HRID == LinkRole.Symbol && link.ChildItemRevisionGUID == SymbolRevision);
        Assert.Equal(4, links.Count);
    }

    [Fact]
    public void SetDataComesFromLinkNormalizer()
    {
        var (links, _) = RevisionService.BuildLinks([], Set(Normal, Least), NewParent);

        Assert.Equal(LinkNormalizer.FootprintData(0, true), Footprints(links)[0].Data);
        Assert.Equal(LinkNormalizer.FootprintData(1, false), Footprints(links)[1].Data);
    }

    [Fact]
    public void SetReplacesExistingSingleFootprint()
    {
        List<ALU_ItemRevisionLink> source = [Symbol(), Link("PCBLIB", Extra, MainData, "L0")];

        var (links, _) = RevisionService.BuildLinks(source, Set(Normal, Least), NewParent);

        Assert.Equal(
            [("PCBLIB", Normal, MainData), ("PCBLIB 1", Least, FirstExtraData)],
            Footprints(links));
        Assert.DoesNotContain(links, link => link.ChildItemRevisionGUID == Extra);
    }

    // ---------- changing the primary, removal, an empty set ----------

    [Fact]
    public void ReorderingChangesDefaultFootprint()
    {
        var (links, changed) = RevisionService.BuildLinks(ThreeFootprints(), Set(Least, Normal, Most), NewParent);

        Assert.Equal(
            [("PCBLIB", Least, MainData), ("PCBLIB 1", Normal, FirstExtraData), ("PCBLIB 2", Most, SecondExtraData)],
            Footprints(links));
        Assert.Single(changed);
        Assert.Contains(Least, changed[0]);
    }

    [Fact]
    public void ReorderingKeepsLinkObjectsOfSameTargets()
    {
        var source = ThreeFootprints();
        source[2].LinkParameters = new _ALU_ItemRevisionLinkParameterList();

        var (links, _) = RevisionService.BuildLinks(source, Set(Least, Normal, Most), NewParent);

        // The service fields of the link (parameters) go with the target and do not stay in place.
        ALU_ItemRevisionLink least = Assert.Single(links, link => link.ChildItemRevisionGUID == Least);
        Assert.Equal("PCBLIB", least.HRID);
        Assert.NotNull(least.LinkParameters);
    }

    [Fact]
    public void RemovingOneOfThreeRenumbersTheRest()
    {
        // The middle one was removed: "PCBLIB 2" becomes "PCBLIB 1" with index 1 — the numbers go in a row.
        var (links, changed) = RevisionService.BuildLinks(ThreeFootprints(), Set(Normal, Most), NewParent);

        Assert.Equal(
            [("PCBLIB", Normal, MainData), ("PCBLIB 1", Most, FirstExtraData)],
            Footprints(links));
        Assert.DoesNotContain(links, link => link.ChildItemRevisionGUID == Least);
        Assert.Single(changed);
    }

    [Fact]
    public void RemovingMainPromotesNext()
    {
        var (links, _) = RevisionService.BuildLinks(ThreeFootprints(), Set(Least, Most), NewParent);

        Assert.Equal(
            [("PCBLIB", Least, MainData), ("PCBLIB 1", Most, FirstExtraData)],
            Footprints(links));
    }

    [Fact]
    public void EmptySetRemovesAllFootprints()
    {
        var (links, changed) = RevisionService.BuildLinks(ThreeFootprints(), Set(), NewParent);

        Assert.Empty(Footprints(links));
        Assert.Equal([SymbolRevision], links.Select(link => link.ChildItemRevisionGUID));
        Assert.Single(changed);
    }

    [Fact]
    public void EmptySetWithoutFootprintsChangesNothing()
    {
        var (links, changed) = RevisionService.BuildLinks([Symbol()], Set(), NewParent);

        Assert.Single(links);
        Assert.Empty(changed);
    }

    [Fact]
    public void AddingToSetKeepsExistingOrder()
    {
        var (links, _) = RevisionService.BuildLinks(
            ThreeFootprints(), Set(Normal, Least, Most, Extra), NewParent);

        Assert.Equal(
            [
                ("PCBLIB", Normal, MainData),
                ("PCBLIB 1", Least, FirstExtraData),
                ("PCBLIB 2", Most, SecondExtraData),
                ("PCBLIB 3", Extra, "{\"Footprint\":{\"FootprintIndex\":3,\"IsDefaultFootprint\":false}}"),
            ],
            Footprints(links));
    }

    // ---------- repeat with the same set ----------

    [Fact]
    public void RepeatingSameSetChangesNothing()
    {
        var source = ThreeFootprints();
        var before = source.Select(link => (link.HRID, link.ChildItemRevisionGUID, link.Data)).ToList();

        var (links, changed) = RevisionService.BuildLinks(source, Set(Normal, Least, Most), NewParent);

        Assert.Empty(changed);
        Assert.Equal(before, links.Select(link => (link.HRID, link.ChildItemRevisionGUID, link.Data)).ToList());
    }

    [Fact]
    public void ChangesIsFalseForSameSetAndTrueOtherwise()
    {
        var links = ThreeFootprints();

        Assert.False(FootprintLinks.Changes(new LinkAssignment(LinkRole.Footprints, null, [Normal, Least, Most]), links));
        Assert.True(FootprintLinks.Changes(new LinkAssignment(LinkRole.Footprints, null, [Least, Normal, Most]), links));
        Assert.True(FootprintLinks.Changes(new LinkAssignment(LinkRole.Footprints, null, [Normal, Least]), links));
        Assert.True(FootprintLinks.Changes(new LinkAssignment(LinkRole.Footprints, null, [Normal, Least, Most, Extra]), links));
        Assert.True(FootprintLinks.Changes(new LinkAssignment(LinkRole.Footprints, null, []), links));
    }

    [Fact]
    public void ChangesDoesNotModifyLinks()
    {
        var links = ThreeFootprints();

        FootprintLinks.Changes(new LinkAssignment(LinkRole.Footprints, null, [Most]), links);

        Assert.Equal(
            [("PCBLIB", Normal, MainData), ("PCBLIB 1", Least, FirstExtraData), ("PCBLIB 2", Most, SecondExtraData)],
            Footprints(links));
    }

    [Fact]
    public void SameSetIsIgnoringTargetCase()
    {
        var links = ThreeFootprints();

        Assert.False(FootprintLinks.Changes(
            new LinkAssignment(LinkRole.Footprints, null, [Normal.ToLowerInvariant(), Least, Most]), links));
    }

    [Fact]
    public void SameTargetsWithoutDataAreRewritten()
    {
        // The link exists, but without footprint data: the set is written again, as Altium does.
        List<ALU_ItemRevisionLink> source = [Link("PCBLIB", Normal, data: null, guid: "L0")];

        Assert.True(FootprintLinks.Changes(new LinkAssignment(LinkRole.Footprints, null, [Normal]), source));

        var (links, _) = RevisionService.BuildLinks(source, Set(Normal), NewParent);
        Assert.Equal([("PCBLIB", Normal, MainData)], Footprints(links));
    }

    // ---------- the single footprint role ----------

    [Fact]
    public void SingleFootprintChangesOnlyMainOfThree()
    {
        var change = new ComponentChange { Links = [new LinkAssignment(LinkRole.Footprint, Extra)] };

        var (links, changed) = RevisionService.BuildLinks(ThreeFootprints(), change, NewParent);

        Assert.Equal(
            [("PCBLIB", Extra, MainData), ("PCBLIB 1", Least, FirstExtraData), ("PCBLIB 2", Most, SecondExtraData)],
            Footprints(links));
        Assert.Single(changed);
    }

    [Fact]
    public void SingleFootprintSameTargetChangesNothing()
    {
        var change = new ComponentChange { Links = [new LinkAssignment(LinkRole.Footprint, Normal)] };

        var (_, changed) = RevisionService.BuildLinks(ThreeFootprints(), change, NewParent);

        Assert.Empty(changed);
    }

    [Fact]
    public void SingleFootprintOnDetailWithoutFootprintAddsMain()
    {
        var change = new ComponentChange { Links = [new LinkAssignment(LinkRole.Footprint, Normal)] };

        var (links, _) = RevisionService.BuildLinks([Symbol()], change, NewParent);

        ALU_ItemRevisionLink footprint = Assert.Single(links, link => LinkRole.IsFootprint(link.HRID));
        Assert.Equal("PCBLIB", footprint.HRID);
        Assert.Equal(Normal, footprint.ChildItemRevisionGUID);
    }

    [Fact]
    public void SingleFootprintWithEmptyTargetRefusedWhenSeveralFootprints()
    {
        var change = new ComponentChange { Links = [new LinkAssignment(LinkRole.Footprint, null)] };

        var error = Assert.Throws<FootprintSetException>(() => RevisionService.BuildLinks(ThreeFootprints(), change, NewParent));

        Assert.Equal(3, error.Count);
        Assert.Contains("3 footprints", error.Message);
        Assert.Contains("footprints", error.Message);
        Assert.Contains("the first in the list becomes primary", error.Message);
        Assert.Contains("empty footprints list", error.Message);
        Assert.StartsWith("CMP-000-0960 has 3 footprints;", error.DescribeFor("CMP-000-0960"));
        Assert.IsAssignableFrom<InvalidOperationException>(error); // RevisionBatch rejects the part, the others go on
    }

    [Fact]
    public void SingleFootprintWithEmptyTargetRefusedForTwoFootprintsToo()
    {
        var change = new ComponentChange { Links = [new LinkAssignment(LinkRole.Footprint, null)] };
        var links = new List<ALU_ItemRevisionLink>
        {
            Symbol(),
            Link("PCBLIB", Normal, MainData, "L0"),
            Link("PCBLIB 1", Least, FirstExtraData, "L1"),
        };

        Assert.Throws<FootprintSetException>(() => RevisionService.BuildLinks(links, change, NewParent));
        Assert.Throws<FootprintSetException>(() => FootprintLinks.Changes(change.Links[0], links));
    }

    [Fact]
    public void SingleFootprintWithEmptyTargetRemovesTheOnlyFootprint()
    {
        var change = new ComponentChange { Links = [new LinkAssignment(LinkRole.Footprint, null)] };
        var links = new List<ALU_ItemRevisionLink> { Symbol(), Link("PCBLIB", Normal, MainData, "L0") };

        var (result, changed) = RevisionService.BuildLinks(links, change, NewParent);

        Assert.Empty(Footprints(result));
        Assert.Single(result, link => link.HRID == LinkRole.Symbol);
        Assert.Single(changed);
    }

    [Fact]
    public void EmptySetStillRemovesAllFootprints()
    {
        var (links, changed) = RevisionService.BuildLinks(ThreeFootprints(), Set(), NewParent);

        Assert.Empty(Footprints(links));
        Assert.Single(changed);
    }

    [Fact]
    public void SingleFootprintWithEmptyTargetOnDetailWithoutFootprintChangesNothing()
    {
        var change = new ComponentChange { Links = [new LinkAssignment(LinkRole.Footprint, null)] };

        var (_, changed) = RevisionService.BuildLinks([Symbol()], change, NewParent);

        Assert.Empty(changed);
    }


    // ---------- adding an additional one (footprintMode = add) ----------

    [Fact]
    public void AppendAddsFootprintWithNextNumber()
    {
        var change = new ComponentChange { Links = [new LinkAssignment(LinkRole.Footprint, Extra, Append: true)] };

        var (links, changed) = RevisionService.BuildLinks(ThreeFootprints(), change, NewParent);

        Assert.Equal(
            [
                ("PCBLIB", Normal, MainData),
                ("PCBLIB 1", Least, FirstExtraData),
                ("PCBLIB 2", Most, SecondExtraData),
                ("PCBLIB 3", Extra, "{\"Footprint\":{\"FootprintIndex\":3,\"IsDefaultFootprint\":false}}"),
            ],
            Footprints(links));
        Assert.Single(changed);
    }

    [Fact]
    public void AppendToDetailWithoutFootprintMakesItMain()
    {
        var change = new ComponentChange { Links = [new LinkAssignment(LinkRole.Footprint, Normal, Append: true)] };

        var (links, _) = RevisionService.BuildLinks([Symbol()], change, NewParent);

        Assert.Equal([("PCBLIB", Normal, MainData)], Footprints(links));
    }

    [Fact]
    public void AppendOfAlreadyLinkedFootprintChangesNothing()
    {
        var change = new ComponentChange { Links = [new LinkAssignment(LinkRole.Footprint, Least, Append: true)] };

        var (links, changed) = RevisionService.BuildLinks(ThreeFootprints(), change, NewParent);

        Assert.Empty(changed);
        Assert.Equal(4, links.Count);
    }

    [Fact]
    public void AppendDoesNotReuseOccupiedNumber()
    {
        // There are "PCBLIB" and "PCBLIB 2" (number 1 is free, but the one after the largest is taken).
        List<ALU_ItemRevisionLink> source =
        [
            Link("PCBLIB", Normal, MainData),
            Link("PCBLIB 2", Most, SecondExtraData),
        ];
        var change = new ComponentChange { Links = [new LinkAssignment(LinkRole.Footprint, Extra, Append: true)] };

        var (links, _) = RevisionService.BuildLinks(source, change, NewParent);

        Assert.Contains(("PCBLIB 3", Extra, "{\"Footprint\":{\"FootprintIndex\":3,\"IsDefaultFootprint\":false}}"), Footprints(links));
    }

    // ---------- contradictory assignments ----------

    [Fact]
    public void FootprintAndFootprintsTogetherAreRejected()
    {
        var change = new ComponentChange
        {
            Links =
            [
                new LinkAssignment(LinkRole.Footprint, Normal),
                new LinkAssignment(LinkRole.Footprints, null, [Least]),
            ],
        };

        var error = Assert.Throws<InvalidOperationException>(() => RevisionService.BuildLinks([], change, NewParent));
        Assert.Contains("footprints", error.Message);
    }

    [Fact]
    public void NumberedRoleCannotBeAssignedDirectly()
    {
        var change = new ComponentChange { Links = [new LinkAssignment("PCBLIB 1", Normal)] };

        var error = Assert.Throws<InvalidOperationException>(() => RevisionService.BuildLinks([], change, NewParent));
        Assert.Contains("PCBLIB 1", error.Message);
    }

    // ---------- numbering in LinkNormalizer.ApplyModelData ----------

    private static List<LinkCorrection> Normalize(params ALU_ItemRevisionLink[] links)
    {
        var corrections = new List<LinkCorrection>();
        LinkNormalizer.ApplyModelData(links, corrections);
        return corrections;
    }

    [Fact]
    public void NumberingDoesNotRepeatOccupiedFootprint1()
    {
        var main = Link("PCBLIB", Normal, MainData);
        var first = Link("PCBLIB 1", Least, FirstExtraData);
        var fresh = Link("PCBLIB", Extra);

        Normalize(main, first, fresh);

        // The primary and "PCBLIB 1" are taken: the new link gets 2 and the role "PCBLIB 2", not a repeat.
        Assert.Equal("PCBLIB 2", fresh.HRID);
        Assert.Equal(SecondExtraData, fresh.Data);
        Assert.Equal(MainData, main.Data);
        Assert.Equal(FirstExtraData, first.Data);
    }

    [Fact]
    public void NumberingTakesFreeMainWhenOnlyAdditionalExist()
    {
        var first = Link("PCBLIB 1", Least, FirstExtraData);
        var fresh = Link("PCBLIB", Normal);

        Normalize(first, fresh);

        Assert.Equal("PCBLIB", fresh.HRID);
        Assert.Equal(MainData, fresh.Data);
    }

    [Fact]
    public void NumberingOfSeveralWithoutDataGoesInOrderMainFirst()
    {
        // The order in the list is "reversed": the primary link is still processed first.
        var second = Link("PCBLIB 1", Least);
        var third = Link("PCBLIB 2", Most);
        var main = Link("PCBLIB", Normal);

        Normalize(second, third, main);

        Assert.Equal(("PCBLIB", MainData), (main.HRID, main.Data));
        Assert.Equal(("PCBLIB 1", FirstExtraData), (second.HRID, second.Data));
        Assert.Equal(("PCBLIB 2", SecondExtraData), (third.HRID, third.Data));
    }

    [Fact]
    public void NumberingOfSeveralOldStyleMainLinksRenumbersExtras()
    {
        // Old parts: several links with the role "PCBLIB" without data.
        var one = Link("PCBLIB", Normal);
        var two = Link("PCBLIB", Least);
        var three = Link("PCBLIB", Most);

        Normalize(one, two, three);

        Assert.Equal(("PCBLIB", MainData), (one.HRID, one.Data));
        Assert.Equal(("PCBLIB 1", FirstExtraData), (two.HRID, two.Data));
        Assert.Equal(("PCBLIB 2", SecondExtraData), (three.HRID, three.Data));
    }

    [Fact]
    public void NumberingOfAdditionalWithoutDataIsNeverDefault()
    {
        var extra = Link("PCBLIB 1", Least);

        Normalize(extra);

        Assert.Equal("PCBLIB 1", extra.HRID);
        Assert.Equal(FirstExtraData, extra.Data);
    }

    [Fact]
    public void NumberingLeavesForeignDataAlone()
    {
        var foreign = Link("PCBLIB 1", Least, "{\"Other\":1}");

        var corrections = Normalize(foreign);

        Assert.Equal("{\"Other\":1}", foreign.Data);
        Assert.Empty(corrections);
    }

    [Fact]
    public void NumberingIsPerRevision()
    {
        var other = Link("PCBLIB", Normal);
        other.ParentItemRevisionGUID = NewParent;
        var first = Link("PCBLIB", Least);

        Normalize(first, other);

        Assert.Equal(MainData, first.Data);
        Assert.Equal(MainData, other.Data);
    }

    // ---------- set check ----------

    [Fact]
    public void CheckAcceptsAltiumReferenceSet()
    {
        Assert.Empty(FootprintLinks.Check(ThreeFootprints()));
    }

    [Fact]
    public void CheckAcceptsSingleFootprintAndNoFootprints()
    {
        Assert.Empty(FootprintLinks.Check([Symbol(), Link("PCBLIB", Normal, MainData)]));
        Assert.Empty(FootprintLinks.Check([Symbol()]));
    }

    [Fact]
    public void CheckReportsTwoDefaults()
    {
        List<ALU_ItemRevisionLink> links =
        [
            Link("PCBLIB", Normal, MainData),
            Link("PCBLIB 1", Least, "{\"Footprint\":{\"FootprintIndex\":1,\"IsDefaultFootprint\":true}}"),
        ];

        var violations = FootprintLinks.Check(links);

        Assert.Contains(violations, violation => violation.Detail.Contains("2 primary footprints"));
    }

    [Fact]
    public void CheckReportsRepeatedIndex()
    {
        List<ALU_ItemRevisionLink> links =
        [
            Link("PCBLIB", Normal, MainData),
            Link("PCBLIB 1", Least, FirstExtraData),
            Link("PCBLIB 2", Most, FirstExtraData),
        ];

        var violations = FootprintLinks.Check(links);

        Assert.Contains(violations, violation => violation.Detail.Contains("FootprintIndex=1 is repeated"));
    }

    [Fact]
    public void CheckReportsNumberedRoleWithOtherIndex()
    {
        List<ALU_ItemRevisionLink> links =
        [
            Link("PCBLIB", Normal, MainData),
            Link("PCBLIB 1", Least, SecondExtraData),
        ];

        var violations = FootprintLinks.Check(links);

        var violation = Assert.Single(violations);
        Assert.Contains("PCBLIB 1", violation.Detail);
        Assert.Contains("FootprintIndex=2", violation.Detail);
        Assert.Contains("should be 1", violation.Detail);
    }

    [Fact]
    public void CheckReportsMainRoleWithNonZeroIndex()
    {
        var violations = FootprintLinks.Check([Link("PCBLIB", Normal, FirstExtraData)]);

        Assert.Contains(violations, violation => violation.Detail.Contains("should be 0"));
    }

    [Fact]
    public void CheckReportsMissingDefaultAmongSeveral()
    {
        List<ALU_ItemRevisionLink> links =
        [
            Link("PCBLIB 1", Least, FirstExtraData),
            Link("PCBLIB 2", Most, SecondExtraData),
        ];

        var violations = FootprintLinks.Check(links);

        Assert.Contains(violations, violation => violation.Detail.Contains("no primary"));
    }

    [Fact]
    public void CheckIgnoresLinksWithoutDataAndDoesNotModify()
    {
        // A link without data is a separate problem (FootprintData), the set is not judged by it.
        var links = new List<ALU_ItemRevisionLink> { Link("PCBLIB", Normal), Link("PCBLIB 1", Least) };

        Assert.Empty(FootprintLinks.Check(links));
        Assert.All(links, link => Assert.Null(link.Data));
    }

    [Fact]
    public void InspectFlagsNumberedFootprintWithoutData()
    {
        var revision = new ALU_ItemRevision { GUID = Parent, RevisionId = "1" };
        var record = new ComponentRecord
        {
            ItemGuid = Guid.NewGuid().ToString(),
            Hrid = "CMP-000-0002",
            FolderPath = @"Components\X",
            FolderGuid = Guid.NewGuid().ToString(),
            LatestRevision = revision,
        };

        var issues = ComponentHealthService.Inspect(
            record,
            [Link("PCBLIB", Normal, MainData), Link("PCBLIB 1", Least)],
            []);

        Assert.Contains(issues, issue => issue.Kind == ComponentHealthService.Kinds.FootprintData && issue.Detail.Contains(Least));
    }

    [Fact]
    public void InspectReportsSetViolationAsNotFixable()
    {
        var record = new ComponentRecord
        {
            ItemGuid = Guid.NewGuid().ToString(),
            Hrid = "CMP-000-0002",
            FolderPath = @"Components\X",
            FolderGuid = Guid.NewGuid().ToString(),
            LatestRevision = new ALU_ItemRevision { GUID = Parent, RevisionId = "1" },
        };

        var issues = ComponentHealthService.Inspect(
            record,
            [
                Link("PCBLIB", Normal, MainData, "L0"),
                Link("PCBLIB 1", Least, SecondExtraData, "L1"),
            ],
            []);

        ComponentIssue issue = Assert.Single(issues, item => item.Kind == ComponentHealthService.Kinds.FootprintSet);
        Assert.False(issue.Fixable);
        Assert.Contains("footprints", issue.Detail);
    }

    // ---------- link mode ----------

    [Theory]
    [InlineData(null, FootprintMode.Replace)]
    [InlineData("", FootprintMode.Replace)]
    [InlineData("replace", FootprintMode.Replace)]
    [InlineData("REPLACE", FootprintMode.Replace)]
    [InlineData("add", FootprintMode.Add)]
    [InlineData(" Add ", FootprintMode.Add)]
    public void FootprintModeIsParsed(string? text, FootprintMode expected) =>
        Assert.Equal(expected, FootprintModes.Parse(text));

    [Fact]
    public void UnknownFootprintModeIsRejectedWithHint()
    {
        var error = Assert.Throws<ArgumentException>(() => FootprintModes.Parse("append"));

        Assert.Contains("replace", error.Message);
        Assert.Contains("add", error.Message);
    }

    // ---------- check of vault_set_links assignments ----------

    private static LinkGroupSpec Group(params LinkTargetSpec[] links) => new(["CMP-1"], links);

    [Fact]
    public void ValidateAcceptsFootprintsWithTargets()
    {
        var groups = LinkGroups.Validate([Group(new LinkTargetSpec("footprints", null, [" PCC-1 ", "PCC-2"]))]);

        LinkTargetSpec link = Assert.Single(groups[0].Links);
        Assert.Equal(LinkRole.Footprints, link.Role);
        Assert.Equal(["PCC-1", "PCC-2"], LinkGroups.TargetsOf(link));
    }

    [Fact]
    public void ValidateAcceptsEmptyFootprintsList()
    {
        var groups = LinkGroups.Validate([Group(new LinkTargetSpec("footprints", null, []))]);

        Assert.Empty(LinkGroups.TargetsOf(groups[0].Links[0]));
    }

    [Fact]
    public void ValidateRejectsFootprintsWithoutTargets()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            LinkGroups.Validate([Group(new LinkTargetSpec("footprints", null, null))]));

        Assert.Contains("targets", error.Message);
    }

    [Fact]
    public void ValidateRejectsFootprintsWithSingleTarget()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            LinkGroups.Validate([Group(new LinkTargetSpec("footprints", "PCC-1", ["PCC-1"]))]));

        Assert.Contains("targets", error.Message);
    }

    [Fact]
    public void ValidateRejectsEmptyAndRepeatedTargetsInSet()
    {
        Assert.Throws<ArgumentException>(() =>
            LinkGroups.Validate([Group(new LinkTargetSpec("footprints", null, ["PCC-1", ""]))]));

        var error = Assert.Throws<ArgumentException>(() =>
            LinkGroups.Validate([Group(new LinkTargetSpec("footprints", null, ["PCC-1", "pcc-1"]))]));
        Assert.Contains("repeated", error.Message);
    }

    [Fact]
    public void ValidateRejectsTargetsForOtherRoles()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            LinkGroups.Validate([Group(new LinkTargetSpec("symbol", null, ["SYM-1"]))]));

        Assert.Contains("only for the footprints role", error.Message);
    }

    [Fact]
    public void ValidateRejectsDirectNumberedRole()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            LinkGroups.Validate([Group(new LinkTargetSpec("PCBLIB 1", "PCC-1"))]));

        Assert.Contains("footprints", error.Message);
    }

    [Fact]
    public void ValidateRejectsFootprintAndFootprintsInOneGroup()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            LinkGroups.Validate([Group(
                new LinkTargetSpec("footprint", "PCC-1"),
                new LinkTargetSpec("footprints", null, ["PCC-2"]))]));

        Assert.Contains("the role is given twice", error.Message);
    }

    [Fact]
    public void OverlapBetweenFootprintAndFootprintsIsReported()
    {
        var groups = LinkGroups.Validate([
            new LinkGroupSpec(["CMP-1"], [new LinkTargetSpec("footprint", "PCC-1")]),
            new LinkGroupSpec(["cmp-1"], [new LinkTargetSpec("footprints", null, ["PCC-2", "PCC-3"])]),
        ]);

        var overlap = Assert.Single(LinkGroups.FindOverlaps(groups));
        Assert.Equal([1, 2], overlap.Groups);
    }

    [Fact]
    public void FootprintsAndSymbolInOneGroupDoNotConflict()
    {
        var groups = LinkGroups.Validate([Group(
            new LinkTargetSpec("footprints", null, ["PCC-1", "PCC-2"]),
            new LinkTargetSpec("symbol", "SYM-1"))]);

        Assert.Equal(2, groups[0].Links.Count);
        Assert.Empty(LinkGroups.FindOverlaps(groups));
    }

    [Fact]
    public void ValidateFootprintRoleNamesTheCopyInMessage()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            LinkGroups.ValidateFootprintRole("Copy 3", new LinkTargetSpec("footprints", null, null)));

        Assert.StartsWith("Copy 3", error.Message);
    }
}
