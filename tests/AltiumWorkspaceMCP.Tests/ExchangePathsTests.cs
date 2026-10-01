using AltiumWorkspaceMCP.Configuration;
using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Tests;

public sealed class ExchangePathsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "altium-exchange-test");

    private static ExchangePaths Make(string? agentRoot) =>
        new(new VaultOptions
        {
            BaseUrl = new Uri("http://localhost:9780"),
            StateDirectory = Path.GetTempPath(),
            ExchangeDirectory = Root,
            ExchangeAgentDirectory = agentRoot,
        });

    [Fact]
    public void AgentPathIsBuiltFromWindowsPath()
    {
        ExchangePaths paths = Make("/home/agent/libs/");

        Assert.Equal("/home/agent/libs", paths.AgentRoot);
        Assert.Equal("/home/agent/libs", paths.ToAgentPath(Root));
        Assert.Equal(
            "/home/agent/libs/CMP-001/model.SchLib",
            paths.ToAgentPath(Path.Combine(Root, "CMP-001", "model.SchLib")));
    }

    [Fact]
    public void PathOutsideExchangeHasNoAgentPath()
    {
        ExchangePaths paths = Make("/home/agent/libs");

        Assert.Null(paths.ToAgentPath(Path.Combine(Path.GetTempPath(), "other", "file.txt")));
    }

    [Fact]
    public void AgentPathBecomesWindowsPathInBothSpellings()
    {
        ExchangePaths paths = Make("/home/agent/libs");
        string expected = Path.Combine(Root, "CMP-001", "model.SchLib");

        Assert.Equal(expected, paths.ToWindowsPath("/home/agent/libs/CMP-001/model.SchLib"));
        Assert.Equal(expected, paths.ToWindowsPath(expected));
        Assert.Equal(Path.GetFullPath(Root), paths.ToWindowsPath("/home/agent/libs"));
    }

    [Fact]
    public void SimilarPrefixIsNotTakenForAgentRoot()
    {
        ExchangePaths paths = Make("/home/agent/libs");

        // "libs-other" is not inside "libs": the path is treated as an ordinary Windows path.
        string result = paths.ToWindowsPath("/home/agent/libs-other/file");

        Assert.DoesNotContain(Root, result);
    }

    [Fact]
    public void WithoutMappingPathsAreLeftAlone()
    {
        ExchangePaths paths = Make(null);

        Assert.Null(paths.AgentRoot);
        Assert.Null(paths.ToAgentPath(Path.Combine(Root, "a.SchLib")));
        Assert.Equal(Path.Combine(Root, "a.SchLib"), paths.ToWindowsPath(Path.Combine(Root, "a.SchLib")));
    }
}
