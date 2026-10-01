using AltiumWorkspaceMCP.Configuration;
using AltiumWorkspaceMCP.Tools;
using AltiumWorkspaceMCP.Vault;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace AltiumWorkspaceMCP.Mcp;

/// <summary>Runs the MCP server over standard input/output.</summary>
internal static class McpHost
{
    public static async Task<int> RunAsync(VaultOptions options)
    {
        var builder = Host.CreateApplicationBuilder();

        // Standard output is taken by the MCP protocol: the log goes to stderr only.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<VaultWorkspace>();

        builder.Services
            .AddMcpServer(mcp =>
            {
                mcp.ServerInfo = new Implementation { Name = "altium-vault-mcp", Version = ServerVersion.Current };
                mcp.ServerInstructions = ServerInstructions.For(options.ConfirmThreshold);
            })
            .WithStdioServerTransport()
            .WithReadableToolErrors()
            .WithTools<ExplorerTools>(ResponseJson.Options)
            .WithTools<ComponentPanelTools>(ResponseJson.Options)
            .WithTools<FolderTools>(ResponseJson.Options)
            .WithTools<TypeTools>(ResponseJson.Options)
            .WithTools<EditingTools>(ResponseJson.Options)
            .WithTools<QualityTools>(ResponseJson.Options)
            .WithTools<ModelFileTools>(ResponseJson.Options)
            .WithTools<PlanTools>(ResponseJson.Options)
            .WithTools<SessionTools>(ResponseJson.Options);

        using IHost host = builder.Build();
        await host.RunAsync();
        return 0;
    }
}
