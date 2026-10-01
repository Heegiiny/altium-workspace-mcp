using System.Net;
using System.Text;
using System.Xml.Linq;
using AltiumWorkspaceMCP.Configuration;
using AltiumWorkspaceMCP.Vault;
using Xunit.Abstractions;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>
/// The wire format of the login, check and logout requests: the IDS services do not publish
/// WSDL, so their contracts are written by hand from the captured format, and any divergence
/// (the field order, the namespace, the header) must be caught without a production server.
/// The server here is a stub on a local port that answers with prepared messages.
/// </summary>
public sealed class SessionWireFormatTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly HttpListener _listener = new();
    private readonly string _prefix;
    private readonly string _state = Path.Combine(Path.GetTempPath(), "altium-wire-" + Guid.NewGuid().ToString("N"));
    private readonly List<Received> _received = [];
    private readonly Task _loop;

    private sealed record Received(string Path, string Action, string Body);

    public SessionWireFormatTests(ITestOutputHelper output)
    {
        _output = output;
        int port = FreePort();
        _prefix = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(_prefix);
        _listener.Start();
        _loop = Task.Run(Serve);
    }

    [Fact]
    public async Task LoginByWindowsGoesToIdsNtlmServiceWithEmptyNameAndPassword()
    {
        await using var session = NewSession(userName: null);

        string id = await session.AcquireAsync(CancellationToken.None);

        Assert.Equal("STUB-SESSION", id);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 10, 20, 30, 500, TimeSpan.Zero), (await session.DescribeAsync(CancellationToken.None)).LastUseDate);
        Received call = Assert.Single(_received);
        Assert.Equal("/ids/IdsNtlmService.svc", call.Path);
        Assert.Equal(
            "{http://schemas.xmlsoap.org/soap/envelope/}Envelope("
            + "{http://schemas.xmlsoap.org/soap/envelope/}Header({}APIVersion=2.0)"
            + "{http://schemas.xmlsoap.org/soap/envelope/}Body({http://tempuri.org/}Login("
            + "{http://tempuri.org/}Username={http://tempuri.org/}Password="
            + "{http://tempuri.org/}SecureLogin=false{http://tempuri.org/}LoginOptions=None)))",
            Canonical(call.Body));
    }

    [Fact]
    public async Task LoginByUserNameGoesToIdsServiceWithNameAndPassword()
    {
        await using var session = NewSession(userName: "operator", password: "secret");

        await session.AcquireAsync(CancellationToken.None);

        Received call = Assert.Single(_received);
        Assert.Equal("/ids/IdsService.svc", call.Path);
        Assert.Equal(
            "{http://schemas.xmlsoap.org/soap/envelope/}Envelope("
            + "{http://schemas.xmlsoap.org/soap/envelope/}Header({}APIVersion=2.0)"
            + "{http://schemas.xmlsoap.org/soap/envelope/}Body({http://tempuri.org/}Login("
            + "{http://tempuri.org/}Username=operator{http://tempuri.org/}Password=secret"
            + "{http://tempuri.org/}SecureLogin=false{http://tempuri.org/}LoginOptions=None)))",
            Canonical(call.Body));
    }

    [Fact]
    public async Task SavedSessionIsCheckedByGetSessionInfoWithHeaders()
    {
        await using (var first = NewSession(userName: null))
        {
            await first.AcquireAsync(CancellationToken.None);
        }

        _received.Clear();
        await using var second = NewSession(userName: null);

        string id = await second.AcquireAsync(CancellationToken.None);

        Assert.Equal("STUB-SESSION", id);
        Received call = Assert.Single(_received);
        Assert.Equal("/ids/IdsService.svc", call.Path);
        Assert.Equal(
            "{http://schemas.xmlsoap.org/soap/envelope/}Envelope("
            + "{http://schemas.xmlsoap.org/soap/envelope/}Header({}APIVersion=2.0{}SessionID=STUB-SESSION)"
            + "{http://schemas.xmlsoap.org/soap/envelope/}Body({http://tempuri.org/}GetSessionInfo=))",
            Canonical(call.Body));
    }

    [Fact]
    public async Task SessionResetLogsOutAndLogsInWithKillExistingSession()
    {
        await using var session = NewSession(userName: null);
        await session.AcquireAsync(CancellationToken.None);
        _received.Clear();

        await session.ResetAsync(CancellationToken.None);

        Assert.Equal(2, _received.Count);
        Assert.Equal("/ids/IdsService.svc", _received[0].Path);
        Assert.Equal(
            "{http://schemas.xmlsoap.org/soap/envelope/}Envelope("
            + "{http://schemas.xmlsoap.org/soap/envelope/}Header({}APIVersion=2.0{}SessionID=STUB-SESSION)"
            + "{http://schemas.xmlsoap.org/soap/envelope/}Body({http://tempuri.org/}Logout({http://tempuri.org/}InvalidateTypes=)))",
            Canonical(_received[0].Body));
        Assert.Equal("/ids/IdsNtlmService.svc", _received[1].Path);
        Assert.Contains("{http://tempuri.org/}LoginOptions=KillExistingSession", Canonical(_received[1].Body));
    }

    [Fact]
    public async Task SessionCloseSendsOnlyLogout()
    {
        await using var session = NewSession(userName: null);
        await session.AcquireAsync(CancellationToken.None);
        _received.Clear();

        await session.CloseAsync();

        Received call = Assert.Single(_received);
        Assert.Equal("Logout", call.Action);
    }

    private VaultSession NewSession(string? userName, string? password = null)
    {
        var options = new VaultOptions
        {
            BaseUrl = new Uri(_prefix),
            UserName = userName,
            Password = password,
            StateDirectory = _state,
            ExchangeDirectory = Path.Combine(_state, "exchange"),
        };

        return new VaultSession(options, new VaultEndpoints(options));
    }

    /// <summary>A message without namespace prefixes, spaces and xmlns declarations — for comparison.</summary>
    private static string Canonical(string xml) => Canonical(XDocument.Parse(xml).Root!);

    private static string Canonical(XElement element)
    {
        string name = "{" + element.Name.NamespaceName + "}" + element.Name.LocalName;
        string attributes = string.Concat(element.Attributes()
            .Where(attribute => !attribute.IsNamespaceDeclaration)
            .OrderBy(attribute => attribute.Name.ToString(), StringComparer.Ordinal)
            .Select(attribute => $"[@{attribute.Name}={attribute.Value}]"));

        if (!element.HasElements)
        {
            return name + attributes + "=" + element.Value.Trim();
        }

        return name + attributes + "(" + string.Concat(element.Elements().Select(Canonical)) + ")";
    }

    private async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            string body = await reader.ReadToEndAsync();
            string action = (context.Request.Headers["SOAPAction"] ?? string.Empty).Trim('"');

            lock (_received)
            {
                _received.Add(new Received(context.Request.Url!.AbsolutePath, action, body));
            }

            _output.WriteLine($"{context.Request.Url!.AbsolutePath} [{action}]\n{body}\n");

            string reply = action switch
            {
                "Login" => LoginResult("LoginResponse"),
                "GetSessionInfo" => LoginResult("GetSessionInfoResponse"),
                _ => "<LogoutResponse xmlns=\"http://tempuri.org/\" />",
            };

            byte[] bytes = Encoding.UTF8.GetBytes(
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>" + reply + "</s:Body></s:Envelope>");
            context.Response.ContentType = "text/xml; charset=utf-8";
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    /// <summary>
    /// The response in the order and composition of the real server (captured from Altium Designer): between
    /// SessionId and LastUseDate comes User, after PasswordExpired — AllowedFeatures; the contract
    /// reads only what it needs and skips the rest.
    /// </summary>
    private static string LoginResult(string wrapper) =>
        $"<{wrapper} xmlns=\"http://tempuri.org/\"><LoginResult xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\">"
        + "<SessionId>STUB-SESSION</SessionId>"
        + "<User><GUID>00000000-0000-0000-0000-000000000001</GUID><Name>stub</Name><Groups><item>g</item></Groups></User>"
        + "<LastUseDate>2026-10-01T10:20:30.5Z</LastUseDate>"
        + "<PasswordExpired>false</PasswordExpired>"
        + "<AllowedFeatures><item>Feature_A</item><item>Feature_B</item></AllowedFeatures>"
        + $"</LoginResult></{wrapper}>";

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        _listener.Close();
        _loop.Wait(TimeSpan.FromSeconds(5));
        if (Directory.Exists(_state))
        {
            Directory.Delete(_state, recursive: true);
        }
    }
}
