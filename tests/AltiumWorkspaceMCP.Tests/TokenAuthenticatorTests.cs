using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AltiumWorkspaceMCP.Auth;
using AltiumWorkspaceMCP.Configuration;
using AltiumWorkspaceMCP.Vault;
using AltiumWorkspaceMCP.Vault.Rest;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>
/// Token login as a whole against a stub login service on a local port: the service directory,
/// application scopes, ActionWait, code exchange, refresh, the token file. A real server is not needed.
/// </summary>
public sealed class TokenAuthenticatorTests : IDisposable
{
    private const string ClientId = "test-client";

    private readonly HttpListener _listener = new();
    private readonly string _prefix;
    private readonly string _state = Path.Combine(Path.GetTempPath(), "altium-token-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<Received> _received = new();
    private readonly List<IDisposable> _disposables = [];
    private readonly Task _loop;

    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    // Stub responses that the test replaces.
    private TaskCompletionSource<string> _actionWait = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _scopes = """["openid","offline_access","a365:workspace:abc"]""";
    private Func<Dictionary<string, string>, (int Status, string Body)> _token = _ => (500, "not configured");

    private sealed record Received(string Method, string Path, string Query, string Body);

    public TokenAuthenticatorTests()
    {
        int port = FreePort();
        _prefix = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(_prefix);
        _listener.Start();
        _loop = Task.Run(Serve);
    }

    [Fact]
    public async Task SignInIssuesLinkWaitsForCodeAndExchangesItForTokensWithVerifier()
    {
        TokenAuthenticator auth = NewAuthenticator();
        string access = OAuthClientTests.Jwt(_now.AddMinutes(10), name: "\u041e\u043f\u0435\u0440\u0430\u0442\u043e\u0440");
        _token = form => (200, TokenJson(access, "R1"));

        LoginStatus started = await auth.StartLoginAsync(CancellationToken.None);

        Assert.Equal(LoginState.Pending, started.State);
        Dictionary<string, string> link = Query(started.Url!);
        Assert.Equal(ClientId, link["client_id"]);
        Assert.Equal("code", link["response_type"]);
        Assert.Equal("openid offline_access a365:workspace:abc", link["scope"]);
        Assert.Equal("S256", link["code_challenge_method"]);
        Assert.Equal(_prefix + "unifiedlogin/api/AuthComplete", link["redirect_uri"]);

        // Until the user logs in, the status stays "waiting" and there is no token.
        Assert.Equal(LoginState.Pending, (await auth.GetLoginStatusAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None)).State);
        await Assert.ThrowsAsync<LoginRequiredException>(() => auth.GetAccessTokenAsync(CancellationToken.None));

        _actionWait.SetResult("""{"ActionResult":"Ok","Data":{"code":"CODE-1"}}""");
        LoginStatus done = await auth.GetLoginStatusAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(LoginState.Completed, done.State);
        Assert.Equal(access, await auth.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal("\u041e\u043f\u0435\u0440\u0430\u0442\u043e\u0440", auth.Describe().Account);

        // ActionWait got exactly the state from the link, the code exchange — the verifier for the given challenge.
        Received wait = Assert.Single(Calls("/actionwait/await"));
        Assert.Equal(link["state"], JsonDocument.Parse(wait.Body).RootElement.GetProperty("token").GetString());

        Dictionary<string, string> exchange = Form(Assert.Single(Calls("/unifiedlogin/connect/token")).Body);
        Assert.Equal("authorization_code", exchange["grant_type"]);
        Assert.Equal("CODE-1", exchange["code"]);
        Assert.Equal(ClientId, exchange["client_id"]);
        Assert.Equal(link["redirect_uri"], exchange["redirect_uri"]);
        Assert.Equal(
            link["code_challenge"],
            Pkce.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(exchange["code_verifier"]))));
        Assert.False(exchange.ContainsKey("client_secret"));
    }

    [Fact]
    public async Task WhileSignInWaitsRepeatedRequestReturnsSameLink()
    {
        TokenAuthenticator auth = NewAuthenticator();

        LoginStatus first = await auth.StartLoginAsync(CancellationToken.None);
        LoginStatus second = await auth.StartLoginAsync(CancellationToken.None);

        Assert.Equal(first.Url, second.Url);
        Assert.Single(Calls("/unifiedlogin/api/ClientScopes"));
    }

    [Fact]
    public async Task BrowserRefusalGivesFailedWithReasonAndNoTokens()
    {
        TokenAuthenticator auth = NewAuthenticator();

        await auth.StartLoginAsync(CancellationToken.None);
        _actionWait.SetResult("""{"Data":{"error":"access_denied"}}""");
        LoginStatus status = await auth.GetLoginStatusAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(LoginState.Failed, status.State);
        Assert.Contains("access_denied", status.Error);
        Assert.False(auth.Describe().HasTokens);
    }

    [Fact]
    public async Task UnknownApplicationIsDetectedByEmptyScopes()
    {
        TokenAuthenticator auth = NewAuthenticator();
        _scopes = "[]";

        var error = await Assert.ThrowsAsync<OAuthRejectedException>(() => auth.StartLoginAsync(CancellationToken.None));

        Assert.Contains("client_id", error.Message);
        Assert.Empty(Calls("/actionwait/await"));
    }

    [Fact]
    public async Task WithoutClientIdErrorNamesTheVariable()
    {
        TokenAuthenticator auth = NewAuthenticator(clientId: null);

        var start = await Assert.ThrowsAsync<InvalidOperationException>(() => auth.StartLoginAsync(CancellationToken.None));
        var use = await Assert.ThrowsAsync<InvalidOperationException>(() => auth.GetAccessTokenAsync(CancellationToken.None));

        Assert.Contains("ALTIUM_OAUTH_CLIENT_ID", start.Message);
        Assert.Contains("ALTIUM_OAUTH_CLIENT_ID", use.Message);
        Assert.Empty(_received);
    }

    [Fact]
    public async Task WithoutSignInTokenRequestAsksForVaultSessionLogin()
    {
        TokenAuthenticator auth = NewAuthenticator();

        var error = await Assert.ThrowsAsync<LoginRequiredException>(() => auth.GetAccessTokenAsync(CancellationToken.None));

        Assert.Contains("vault_session action=login", error.Message);
    }

    [Fact]
    public async Task TokenIsRefreshedAMinuteBeforeExpiryAndNewRefreshIsSaved()
    {
        TokenAuthenticator auth = NewAuthenticator();
        string first = OAuthClientTests.Jwt(_now.AddMinutes(10));
        string second = OAuthClientTests.Jwt(_now.AddMinutes(70));
        var replies = new Queue<string>([TokenJson(first, "R1"), TokenJson(second, "R2")]);
        _token = _ => (200, replies.Dequeue());

        await LoginAsync(auth);
        Assert.Equal(first, await auth.GetAccessTokenAsync(CancellationToken.None));

        _now = _now.AddMinutes(8); // 2 minutes left — no refresh needed yet
        Assert.Equal(first, await auth.GetAccessTokenAsync(CancellationToken.None));
        Assert.Single(Calls("/unifiedlogin/connect/token"));

        _now = _now.AddSeconds(70); // less than a minute left
        Assert.Equal(second, await auth.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal(second, await auth.GetAccessTokenAsync(CancellationToken.None));

        Dictionary<string, string> refresh = Form(Calls("/unifiedlogin/connect/token").Last().Body);
        Assert.Equal("refresh_token", refresh["grant_type"]);
        Assert.Equal("R1", refresh["refresh_token"]);
        Assert.Equal(ClientId, refresh["client_id"]);
        Assert.Equal(2, Calls("/unifiedlogin/connect/token").Count);
        Assert.True(auth.Describe().HasRefreshToken);
    }

    [Fact]
    public async Task RefreshRefusalForgetsTokensAndAsksToSignInAgain()
    {
        TokenAuthenticator auth = NewAuthenticator(TokenStoreMode.File);
        var replies = new Queue<(int, string)>(
        [
            (200, TokenJson(OAuthClientTests.Jwt(_now.AddMinutes(2)), "R1")),
            (400, """{"error":"invalid_grant"}"""),
        ]);
        _token = _ => replies.Dequeue();

        await LoginAsync(auth);
        Assert.True(File.Exists(Path.Combine(_state, "tokens.bin")));

        _now = _now.AddMinutes(5);
        var error = await Assert.ThrowsAsync<LoginRequiredException>(() => auth.GetAccessTokenAsync(CancellationToken.None));

        Assert.Contains("invalid_grant", error.Message);
        Assert.Contains("vault_session action=login", error.Message);
        Assert.False(auth.Describe().HasTokens);
        Assert.False(File.Exists(Path.Combine(_state, "tokens.bin")));
    }

    [Fact]
    public async Task ServerRejectedTokenOneRefreshAndRepeatWithSameTokenMakesNoRequest()
    {
        TokenAuthenticator auth = NewAuthenticator();
        string first = OAuthClientTests.Jwt(_now.AddHours(1));
        string second = OAuthClientTests.Jwt(_now.AddHours(2));
        var replies = new Queue<string>([TokenJson(first, "R1"), TokenJson(second, "R2")]);
        _token = _ => (200, replies.Dequeue());

        await LoginAsync(auth);

        TokenSet renewed = await auth.RefreshAsync(first, CancellationToken.None);
        Assert.Equal(second, renewed.AccessToken);

        // The same stale token again: already refreshed by another call, there is no new request.
        Assert.Equal(second, (await auth.RefreshAsync(first, CancellationToken.None)).AccessToken);
        Assert.Equal(2, Calls("/unifiedlogin/connect/token").Count);
    }

    [Fact]
    public async Task ByDefaultTokensAreInMemoryAndFileStoresAndRestoresThem()
    {
        string access = OAuthClientTests.Jwt(_now.AddHours(1));
        _token = _ => (200, TokenJson(access, "R1"));

        await LoginAsync(NewAuthenticator(TokenStoreMode.Memory));
        Assert.False(File.Exists(Path.Combine(_state, "tokens.bin")));

        await LoginAsync(NewAuthenticator(TokenStoreMode.File));
        Assert.True(File.Exists(Path.Combine(_state, "tokens.bin")));

        // A new process: the tokens are taken from the file without a new login and without requests to the server.
        _received.Clear();
        TokenAuthenticator restarted = NewAuthenticator(TokenStoreMode.File);
        Assert.Equal(access, await restarted.GetAccessTokenAsync(CancellationToken.None));
        Assert.Empty(_received);

        // Another application's file does not fit.
        TokenAuthenticator other = NewAuthenticator(TokenStoreMode.File, clientId: "other-client");
        Assert.False(other.Describe().HasTokens);
    }

    [Fact]
    public async Task ForgettingTokensRevokesRefreshOnServerAndErasesFile()
    {
        TokenAuthenticator auth = NewAuthenticator(TokenStoreMode.File);
        _token = _ => (200, TokenJson(OAuthClientTests.Jwt(_now.AddHours(1)), "R1"));
        await LoginAsync(auth);

        await auth.ForgetAsync(CancellationToken.None);

        Dictionary<string, string> revoke = Form(Assert.Single(Calls("/unifiedlogin/connect/revocation")).Body);
        Assert.Equal("R1", revoke["token"]);
        Assert.Equal(ClientId, revoke["client_id"]);
        Assert.False(auth.Describe().HasTokens);
        Assert.False(File.Exists(Path.Combine(_state, "tokens.bin")));
    }

    [Fact]
    public async Task TokenModeSessionPassesAccessTokenAsSessionIdentifier()
    {
        TokenAuthenticator auth = NewAuthenticator();
        string access = OAuthClientTests.Jwt(_now.AddHours(1));
        _token = _ => (200, TokenJson(access, "R1"));

        VaultOptions options = Options(TokenStoreMode.Memory, ClientId);
        await using var session = new VaultSession(options, new VaultEndpoints(options), auth);

        var notLoggedIn = await Assert.ThrowsAsync<LoginRequiredException>(() => session.AcquireAsync(CancellationToken.None));
        Assert.Contains("vault_session action=login", notLoggedIn.Message);

        await LoginAsync(auth);
        Assert.Equal(access, await session.AcquireAsync(CancellationToken.None));
        Assert.Equal(access, await session.ExecuteAsync((id, _) => Task.FromResult(id), CancellationToken.None));
        SessionSnapshot snapshot = await session.DescribeAsync(CancellationToken.None);
        Assert.True(snapshot.IsEstablished);
        Assert.True(snapshot.Token!.HasTokens);
        Assert.Null(snapshot.StateFile);
        Assert.Empty(Calls("/ids/IdsService.svc"));
    }

    private async Task LoginAsync(TokenAuthenticator auth)
    {
        _actionWait = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await auth.StartLoginAsync(CancellationToken.None);
        _actionWait.SetResult("""{"ActionResult":"Ok","Data":{"code":"CODE"}}""");

        LoginStatus status = await auth.GetLoginStatusAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal(LoginState.Completed, status.State);
    }

    private VaultOptions Options(TokenStoreMode store, string? clientId) => new()
    {
        BaseUrl = new Uri(_prefix),
        AuthMethod = AuthMethod.Token,
        OAuthClientId = clientId,
        TokenStore = store,
        StateDirectory = _state,
        ExchangeDirectory = Path.Combine(_state, "exchange"),
    };

    private TokenAuthenticator NewAuthenticator(TokenStoreMode store = TokenStoreMode.Memory, string? clientId = ClientId)
    {
        VaultOptions options = Options(store, clientId);
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var auth = new TokenAuthenticator(
            options, new OAuthClient(http, new ServiceDirectory(options.BaseUrl, http)), () => _now);
        _disposables.Add(auth);
        _disposables.Add(http);
        return auth;
    }

    private static string TokenJson(string access, string refresh) =>
        JsonSerializer.Serialize(new { access_token = access, refresh_token = refresh, id_token = access, token_type = "Bearer", expires_in = 600 });

    private List<Received> Calls(string path) => _received.Where(call => call.Path == path).ToList();

    private static Dictionary<string, string> Query(Uri url) => Form(url.Query.TrimStart('?'));

    private static Dictionary<string, string> Form(string text) => text.Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(pair => pair.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));

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

            // ActionWait keeps the request open, so each request is served separately.
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            string body = await reader.ReadToEndAsync();
            string path = context.Request.Url!.AbsolutePath;
            _received.Enqueue(new Received(context.Request.HttpMethod, path, context.Request.Url.Query, body));

            (int status, string reply, string type) = path switch
            {
                "/servicediscovery/servicediscovery.asmx" => (200, Discovery(), "text/xml"),
                "/unifiedlogin/api/ClientScopes" => (200, _scopes, "application/json"),
                "/actionwait/await" => (200, await _actionWait.Task, "application/json"),
                "/unifiedlogin/connect/token" => Token(body),
                "/unifiedlogin/connect/revocation" => (200, "{}", "application/json"),
                _ => (404, "no such address", "text/plain"),
            };

            byte[] bytes = Encoding.UTF8.GetBytes(reply);
            context.Response.StatusCode = status;
            context.Response.ContentType = type;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
        catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or IOException)
        {
            // The test has already finished and closed the stub.
        }
    }

    private (int, string, string) Token(string body)
    {
        (int status, string reply) = _token(Form(body));
        return (status, reply, "application/json");
    }

    private string Discovery() =>
        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><GetServicesEndPointsResponse xmlns=\"http://altium.com/\">"
        + "<GetServicesEndPointsResult>"
        + $"<EndPointInfo><ServiceKind>AuthService</ServiceKind><ServiceUrl>{_prefix}unifiedlogin</ServiceUrl></EndPointInfo>"
        + $"<EndPointInfo><ServiceKind>ActionWait</ServiceKind><ServiceUrl>{_prefix}actionwait/await</ServiceUrl></EndPointInfo>"
        + "</GetServicesEndPointsResult></GetServicesEndPointsResponse></s:Body></s:Envelope>";

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
        _actionWait.TrySetCanceled();
        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }

        _listener.Close();
        _loop.Wait(TimeSpan.FromSeconds(5));

        if (Directory.Exists(_state))
        {
            Directory.Delete(_state, recursive: true);
        }
    }
}
