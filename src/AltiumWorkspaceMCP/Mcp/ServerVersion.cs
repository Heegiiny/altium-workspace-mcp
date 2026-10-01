using System.Reflection;

namespace AltiumWorkspaceMCP.Mcp;

/// <summary>The server version: <c>&lt;Version&gt;</c> from the project file, shown in vault_status and to MCP clients.</summary>
internal static class ServerVersion
{
    /// <summary>The version without the build metadata (<c>0.1.0+commit</c> → <c>0.1.0</c>).</summary>
    public static string Current { get; } = Parse(
        typeof(ServerVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>Cuts the build metadata off the informational version; an empty value gives "unknown".</summary>
    internal static string Parse(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return "unknown";
        }

        int plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);

        return plus < 0 ? informationalVersion : informationalVersion[..plus];
    }
}
