using AltiumWorkspaceMCP.Mcp;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>The server version (0.1.0): comes from the project file, build metadata is cut off.</summary>
public sealed class ServerVersionTests
{
    [Theory]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("0.1.0+3f2a9c1", "0.1.0")]
    [InlineData("1.2.3-beta.1+abc", "1.2.3-beta.1")]
    [InlineData("", "unknown")]
    [InlineData(null, "unknown")]
    public void ParseCutsBuildMetadata(string? informational, string expected) =>
        Assert.Equal(expected, ServerVersion.Parse(informational));

    [Fact]
    public void CurrentVersionIsTheReleaseVersion() =>
        Assert.Equal("0.1.0", ServerVersion.Current);
}
