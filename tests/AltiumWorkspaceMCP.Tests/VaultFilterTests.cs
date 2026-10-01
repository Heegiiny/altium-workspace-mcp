using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Tests;

public sealed class VaultFilterTests
{
    [Fact]
    public void LiteralDoublesQuotes()
    {
        Assert.Equal("'O''Brien'", VaultFilter.Literal("O'Brien"));
        Assert.Equal("''", VaultFilter.Literal(null));
        Assert.Equal("''''", VaultFilter.Literal("'"));
    }

    [Fact]
    public void QuoteInValueCannotBreakOutOfLiteral()
    {
        string filter = VaultFilter.Equal("HRID", "x' OR 1=1 --");

        Assert.Equal("HRID = 'x'' OR 1=1 --'", filter);
    }

    [Fact]
    public void EqualAndLike()
    {
        Assert.Equal("HRID = 'CMP-001'", VaultFilter.Equal("HRID", "CMP-001"));
        Assert.Equal("HRID LIKE 'CMP-%'", VaultFilter.Like("HRID", "CMP-%"));
    }

    [Theory]
    [InlineData("HRID; DROP")]
    [InlineData("HRID = 1 OR HRID")]
    [InlineData("")]
    [InlineData("  ")]
    public void FieldNameMustBeSimpleIdentifier(string field) =>
        Assert.Throws<ArgumentException>(() => VaultFilter.Equal(field, "x"));

    [Fact]
    public void InJoinsEscapedValues() =>
        Assert.Equal("GUID IN ('a', 'b''c')", VaultFilter.In("GUID", ["a", "b'c"]));

    [Fact]
    public void EmptyInIsAlwaysFalse() =>
        Assert.Equal("1 = 0", VaultFilter.In("GUID", []));

    [Fact]
    public void InAtLimitPassesAndAboveThrows()
    {
        var atLimit = Enumerable.Range(0, VaultFilter.MaxInValues).Select(number => $"v{number}").ToList();
        Assert.StartsWith("GUID IN (", VaultFilter.In("GUID", atLimit));

        var over = atLimit.Append("extra").ToList();
        var error = Assert.Throws<ArgumentException>(() => VaultFilter.In("GUID", over));
        Assert.Contains("Chunk", error.Message);
    }

    [Fact]
    public void ChunkSplitsIntoPartsThatFitIn()
    {
        var values = Enumerable.Range(0, VaultFilter.MaxInValues * 2 + 5).Select(number => $"v{number}").ToList();

        var chunks = VaultFilter.Chunk(values).ToList();

        Assert.Equal(3, chunks.Count);
        Assert.Equal(values.Count, chunks.Sum(chunk => chunk.Count));
        Assert.All(chunks, chunk => VaultFilter.In("GUID", chunk));
    }

    [Fact]
    public void AndOrSkipEmptyPartsAndParenthesizeTheRest()
    {
        Assert.Equal(string.Empty, VaultFilter.And(null, " "));
        Assert.Equal("A = 1", VaultFilter.And("A = 1", null));
        Assert.Equal("(A = 1) AND (B = 2)", VaultFilter.And("A = 1", "B = 2"));
        Assert.Equal("(A = 1) OR (B = 2 AND C = 3)", VaultFilter.Or("A = 1", "B = 2 AND C = 3"));
    }
}
