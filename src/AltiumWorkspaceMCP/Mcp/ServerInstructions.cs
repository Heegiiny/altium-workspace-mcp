using System.Reflection;

namespace AltiumWorkspaceMCP.Mcp;

/// <summary>
/// MCP server instructions: the client passes them to the model on connect — the cheapest and
/// most reliable place for rules the agent must always know, even without skills.
/// </summary>
/// <remarks>
/// The text lives in <c>Mcp/ServerInstructions.md</c> (embedded resource), at most
/// <see cref="MaxLength"/> characters. Detailed instructions — docs/tools.md.
/// </remarks>
internal static class ServerInstructions
{
    /// <summary>Length limit: the instructions enter the model context on every connection.</summary>
    public const int MaxLength = 2500;

    /// <summary>Default confirmation threshold — for the length and content test, without server settings.</summary>
    private const int DefaultConfirmThreshold = 4;

    private static readonly Lazy<string> Template = new(Load);

    /// <summary>Instructions text with the default confirmation threshold.</summary>
    public static string Text => For(DefaultConfirmThreshold);

    /// <summary>Instructions text with the effective confirmation threshold (<c>ALTIUM_CONFIRM_THRESHOLD</c>).</summary>
    public static string For(int confirmThreshold) =>
        Template.Value.Replace("{threshold}", confirmThreshold.ToString(), StringComparison.Ordinal);

    private static string Load()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ServerInstructions.md")
            ?? throw new InvalidOperationException("The assembly has no embedded resource ServerInstructions.md.");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);

        return reader.ReadToEnd().Trim();
    }
}
