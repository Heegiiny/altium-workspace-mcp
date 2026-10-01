using System.Text.Encodings.Web;
using System.Text.Json;
using ModelContextProtocol;

namespace AltiumWorkspaceMCP.Tools;

/// <summary>
/// JSON options for everything the server returns to the caller: the same as the MCP SDK
/// defaults, but non-ASCII letters (Russian part names, descriptions) are written as they are,
/// not as <c>\uXXXX</c> sequences — an escaped letter costs 6 characters of the response
/// budget and extra tokens for the agent.
/// </summary>
internal static class ResponseJson
{
    /// <summary>Serializer options for tool results, size estimates and response shortening.</summary>
    public static JsonSerializerOptions Options { get; } = new(McpJsonUtilities.DefaultOptions)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
