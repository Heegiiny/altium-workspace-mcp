using System.Net;
using System.Net.Sockets;
using AltiumWorkspaceMCP.Configuration;
using AltiumWorkspaceMCP.Tools;
using AltiumWorkspaceMCP.Vault;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AltiumWorkspaceMCP.Mcp;

/// <summary>
/// The same MCP server, but over TCP instead of standard input/output.
/// </summary>
/// <remarks>
/// A client running in a container or on another machine cannot launch a Windows process
/// directly and connects to a port. The bridge removes the need for a separate intermediary
/// program: server and bridge are one application with shared code, settings and, most
/// importantly, a shared vault session, whose slot is limited by the license.
///
/// Connections are served concurrently, but the workspace is shared: a second client
/// connecting does not take a second slot.
/// </remarks>
internal static class BridgeHost
{
    public static async Task<int> RunAsync(VaultOptions options, string[] arguments)
    {
        IPAddress address = arguments.Length > 0 && !string.IsNullOrWhiteSpace(arguments[0])
            ? ParseAddress(arguments[0])
            : IPAddress.Loopback;

        int port = arguments.Length > 1 && int.TryParse(arguments[1], out int parsed) ? parsed : 18792;

        await using var workspace = new VaultWorkspace(options);

        var listener = new TcpListener(address, port);
        listener.Start();

        Console.Error.WriteLine($"MCP bridge listening on {address}:{port}");
        Console.Error.WriteLine($"Workspace: {options.BaseUrl}");
        Console.Error.WriteLine("Stop with Ctrl+C.");

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stopping.Cancel();
        };

        try
        {
            while (!stopping.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(stopping.Token);
                _ = ServeAsync(client, workspace, stopping.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping with Ctrl+C is the normal way to exit.
        }
        finally
        {
            listener.Stop();
        }

        return 0;
    }

    /// <summary>Serves one connection until it is closed.</summary>
    private static async Task ServeAsync(
        TcpClient client,
        VaultWorkspace workspace,
        CancellationToken cancellationToken)
    {
        using (client)
        {
            EndPoint? remote = client.Client.RemoteEndPoint;
            Console.Error.WriteLine($"Connected: {remote}");

            try
            {
                await using NetworkStream stream = client.GetStream();

                var services = new ServiceCollection();
                services.AddSingleton(workspace);
                services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);

                services
                    .AddMcpServer(mcp =>
                    {
                        mcp.ServerInfo = new Implementation
                        {
                            Name = "altium-vault-mcp",
                            Version = ServerVersion.Current,
                        };
                        mcp.ServerInstructions = ServerInstructions.For(workspace.Options.ConfirmThreshold);
                    })
                    .WithStreamServerTransport(stream, stream)
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

                await using ServiceProvider provider = services.BuildServiceProvider();

                McpServer server = provider.GetRequiredService<McpServer>();
                await server.RunAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException)
            {
                // The client disconnected — the normal end of a connection.
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Connection {remote} aborted: {exception.Message}");
            }
            finally
            {
                Console.Error.WriteLine($"Disconnected: {remote}");
            }
        }
    }

    private static IPAddress ParseAddress(string value) =>
        string.Equals(value, "any", StringComparison.OrdinalIgnoreCase)
            ? IPAddress.Any
            : IPAddress.TryParse(value, out IPAddress? parsed)
                ? parsed
                : throw new InvalidOperationException(
                    $"Cannot parse address '{value}'. Specify an IP address or 'any'.");
}
