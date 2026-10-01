using AltiumWorkspaceMCP.Configuration;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>
/// The file exchange directory between the MCP server and the agent.
/// </summary>
/// <remarks>
/// altium-designer-mcp, which edits library files, works next to the agent and sees
/// the file system its own way: in the agent's WSL the Windows directory is mounted at a different
/// path, for example D:\AltiumLibraries as
/// /home/agent/altium-libraries. The MCP server writes files by Windows paths, reports them to the agent
/// in its own form and accepts a path in either of the two forms.
/// </remarks>
public sealed class ExchangePaths
{
    public ExchangePaths(VaultOptions options)
    {
        Root = Path.GetFullPath(options.ExchangeDirectory);
        AgentRoot = string.IsNullOrWhiteSpace(options.ExchangeAgentDirectory)
            ? null
            : options.ExchangeAgentDirectory.Replace('\\', '/').TrimEnd('/');
    }

    /// <summary>The exchange directory in MCP server paths.</summary>
    public string Root { get; }

    /// <summary>The same directory in agent paths; null if no mapping is set.</summary>
    public string? AgentRoot { get; }

    /// <summary>The path as the agent sees it; null if it is outside the exchange directory.</summary>
    public string? ToAgentPath(string windowsPath)
    {
        if (AgentRoot is null)
        {
            return null;
        }

        string relative = Path.GetRelativePath(Root, Path.GetFullPath(windowsPath));

        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }

        return relative == "." ? AgentRoot : $"{AgentRoot}/{relative.Replace('\\', '/')}";
    }

    /// <summary>The MCP server path for a path that came from the agent or from a person.</summary>
    public string ToWindowsPath(string path)
    {
        string normalized = path.Replace('\\', '/');

        if (AgentRoot is not null
            && (normalized == AgentRoot || normalized.StartsWith(AgentRoot + "/", StringComparison.Ordinal)))
        {
            string relative = normalized.Length == AgentRoot.Length ? string.Empty : normalized[(AgentRoot.Length + 1)..];
            return Path.GetFullPath(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));
        }

        return Path.GetFullPath(path);
    }
}
