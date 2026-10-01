using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Tests;

public sealed class LinkTargetIdTests
{
    [Theory]
    [InlineData("PCC-0009-3", "PCC-0009", "3")]
    [InlineData("PCC-0009-12", "PCC-0009", "12")]
    [InlineData("SYM-0001-7", "SYM-0001", "7")]
    public void TryParseSplitsTrailingRevisionNumber(string target, string expectedBaseId, string expectedRevision)
    {
        Assert.True(LinkTargetId.TryParse(target, out string baseId, out string revision));
        Assert.Equal(expectedBaseId, baseId);
        Assert.Equal(expectedRevision, revision);
    }

    // CMP-000-0960 is an identifier as a whole, not CMP-000 revision 960: formally the suffix is
    // numeric, and the function separates it, but the calling code must first look for an item with the full
    // identifier and parse the record only on failure.
    [Fact]
    public void TryParseAlsoSplitsFullIdentifierLookingLikeRevision()
    {
        Assert.True(LinkTargetId.TryParse("CMP-000-0960", out string baseId, out string revision));
        Assert.Equal("CMP-000", baseId);
        Assert.Equal("0960", revision);
    }

    [Theory]
    [InlineData("PCC")]
    [InlineData("")]
    public void TryParseFailsWithoutDash(string target)
    {
        Assert.False(LinkTargetId.TryParse(target, out _, out _));
    }

    [Theory]
    [InlineData("PCC-0009-")]
    [InlineData("-3")]
    [InlineData("PCC-0009-3a")]
    public void TryParseRejectsMalformedInput(string target)
    {
        Assert.False(LinkTargetId.TryParse(target, out _, out _));
    }

    [Fact]
    public void DescribeTargetMarksNonCurrentRevision()
    {
        Assert.Equal("PCC-0009 rev. 3", LinkTargetId.DescribeTarget("PCC-0009", "3", isCurrent: true));
        Assert.Equal(
            "PCC-0009 rev. 2 (not active)",
            LinkTargetId.DescribeTarget("PCC-0009", "2", isCurrent: false));
    }

    [Fact]
    public void DescribeTargetWithoutRevisionIdReturnsHridOnly()
    {
        Assert.Equal("PCC-0009", LinkTargetId.DescribeTarget("PCC-0009", null, isCurrent: false));
    }

    [Fact]
    public void DescribeMissingRevisionCollapsesConsecutiveNumbersIntoRange()
    {
        string message = LinkTargetId.DescribeMissingRevision("PCC-0009", "3", ["1", "2"]);

        Assert.Equal("PCC-0009 has no revision 3; available: 1–2. Without a number the active revision is taken.", message);
    }

    [Fact]
    public void DescribeMissingRevisionListsNonConsecutiveNumbersSeparately()
    {
        string message = LinkTargetId.DescribeMissingRevision("PCC-0009", "9", ["1", "2", "5"]);

        Assert.Equal("PCC-0009 has no revision 9; available: 1–2, 5. Without a number the active revision is taken.", message);
    }

    [Fact]
    public void DescribeMissingRevisionHandlesNoRevisions()
    {
        string message = LinkTargetId.DescribeMissingRevision("PCC-0009", "1", []);

        Assert.Equal("PCC-0009 has no revision 1; no revisions. Without a number the active revision is taken.", message);
    }
}
