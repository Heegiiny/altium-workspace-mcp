using AltiumWorkspaceMCP.Safety;
using AltiumWorkspaceMCP.Vault;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AltiumWorkspaceMCP.Mcp;

/// <summary>
/// Returns the reason for a refusal to the caller instead of a generic message and
/// replaces an over-long result with a clear refusal.
/// </summary>
/// <remarks>
/// By default the MCP server hides the exception text. For this server that is harmful:
/// a safety-rule refusal, a missing component or a Vault service error is information
/// the caller can use to fix the request. The exception texts here are written to be read;
/// and contain nothing secret.
/// </remarks>
internal static class ToolErrorFilter
{
    public static IMcpServerBuilder WithReadableToolErrors(this IMcpServerBuilder builder)
    {
        builder.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
        {
            try
            {
                CallToolResult result = await next(context, cancellationToken);
                return LimitSize(context, result);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = Explain(exception) }],
                };
            }
        }));

        return builder;
    }

    /// <summary>Replaces an over-long result with a refusal and advice (see <see cref="ResponseSizeGuard"/>).</summary>
    private static CallToolResult LimitSize(RequestContext<CallToolRequestParams> context, CallToolResult result)
    {
        var workspace = context.Services?.GetService<VaultWorkspace>();
        if (workspace is null)
        {
            return result;
        }

        bool readOnly = context.MatchedPrimitive is McpServerTool tool
            && tool.ProtocolTool.Annotations?.ReadOnlyHint == true;

        bool dryRun = context.Params?.Arguments is { } arguments
            && arguments.TryGetValue("dryRun", out System.Text.Json.JsonElement flag)
            && flag.ValueKind == System.Text.Json.JsonValueKind.True;

        return ResponseSizeGuard.Limit(
            context.Params?.Name ?? "tool", readOnly, result, workspace.Options.MaxResponseChars, dryRun);
    }

    private static string Explain(Exception exception) => exception switch
    {
        ChangeRejectedException rejected => rejected.Message,

        VaultOperationException vault => vault.Message,

        ArgumentException argument => argument.Message,

        InvalidOperationException invalid => invalid.Message,

        _ => $"{exception.GetType().Name}: {exception.Message}",
    };
}
