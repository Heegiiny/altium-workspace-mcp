using System.ComponentModel;
using System.IO.Pipelines;
using AltiumWorkspaceMCP.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>A Russian part name reaches the client as is, not as \uXXXX sequences.</summary>
public sealed class ResponseEncodingTests
{
    // "Resistor 0805" in Russian, written with escapes so that the source stays ASCII.
    private const string RussianName = "\u0420\u0435\u0437\u0438\u0441\u0442\u043e\u0440 0805";

    [McpServerToolType]
    public sealed class SampleTools
    {
        [McpServerTool(Name = "sample_part")]
        [Description("A tool that returns a part with a Russian name.")]
        public Task<object> GetPartAsync() => Task.FromResult<object>(new { hrid = "CMP-000-0001", comment = RussianName });
    }

    [Fact]
    public async Task ToolResultKeepsRussianLettersUnescaped()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services
            .AddMcpServer()
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithTools<SampleTools>(ResponseJson.Options);

        await using ServiceProvider provider = services.BuildServiceProvider();
        McpServer server = provider.GetRequiredService<McpServer>();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task serverRun = server.RunAsync(cancellation.Token);

        await using McpClient client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()),
            cancellationToken: cancellation.Token);

        CallToolResult result = await client.CallToolAsync("sample_part", cancellationToken: cancellation.Token);

        string text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains(RussianName, text);
        Assert.DoesNotContain("\\u", text);

        await cancellation.CancelAsync();
        await Task.WhenAny(serverRun, Task.Delay(1000));
    }
}
