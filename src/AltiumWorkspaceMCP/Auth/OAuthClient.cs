using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AltiumWorkspaceMCP.Vault.Rest;

namespace AltiumWorkspaceMCP.Auth;

/// <summary>The login server rejected the request (a wrong code, an expired or revoked refresh token, an unknown application).</summary>
public sealed class OAuthRejectedException(string message, string? error) : InvalidOperationException(message)
{
    /// <summary>The OAuth error code (<c>invalid_grant</c>, <c>unauthorized_client</c> etc.) if the server returned one.</summary>
    public string? Error { get; } = error;
}

/// <summary>Addresses of the login service: from the workspace service directory (<c>AuthService</c>, <c>ActionWait</c>).</summary>
public sealed record OAuthEndpoints(Uri AuthService, Uri ActionWait)
{
    private string Root => AuthService.ToString().TrimEnd('/');

    public Uri Authorize => new(Root + "/connect/authorize");

    public Uri Token => new(Root + "/connect/token");

    public Uri Revocation => new(Root + "/connect/revocation");

    /// <summary>Where the login server sends the browser after login; the client does not listen on the port, the code comes through ActionWait.</summary>
    public Uri AuthComplete => new(Root + "/api/AuthComplete");

    public Uri ClientScopes(string clientId) =>
        new($"{Root}/api/ClientScopes?clientId={Uri.EscapeDataString(clientId)}&includeOfflineAccess=True");

    /// <summary>The long-polling address: the service in the directory is already named <c>…/await</c>, but for safety it is appended if missing.</summary>
    public Uri ActionWaitAwait => ActionWait.AbsolutePath.TrimEnd('/').EndsWith("/await", StringComparison.OrdinalIgnoreCase)
        ? ActionWait
        : new Uri(ActionWait.ToString().TrimEnd('/') + "/await");
}

/// <summary>The ActionWait response: the authorization code or a description of the login error.</summary>
public sealed record ActionWaitResult(string? Code, string? Error);

/// <summary>
/// A client of the Altium login service (OAuth2 authorization code + PKCE through the system browser) built from
/// the observed sign-in flow. Only the network exchange and response parsing are here;
/// the token state is kept by <see cref="TokenAuthenticator"/>.
/// </summary>
public sealed class OAuthClient
{
    /// <summary>The browser login wait period — the same as in Altium Designer (300000 ms).</summary>
    public static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly ServiceDirectory _directory;
    private OAuthEndpoints? _endpoints;

    /// <param name="http">A client without its own timeout: the ActionWait long poll is limited here, not by the client.</param>
    /// <param name="directory">The workspace service directory.</param>
    public OAuthClient(HttpClient http, ServiceDirectory directory)
    {
        _http = http;
        _directory = directory;
    }

    public async Task<OAuthEndpoints> ResolveAsync(CancellationToken cancellationToken)
    {
        if (_endpoints is not null)
        {
            return _endpoints;
        }

        // The login client has no timeout of its own (it is held by the long poll), so the service directory is limited here.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        var services = await _directory.GetAllAsync(timeout.Token);

        if (!services.TryGetValue(ServiceDirectory.Kinds.AuthService, out Uri? auth)
            || !services.TryGetValue(ServiceDirectory.Kinds.ActionWait, out Uri? wait))
        {
            throw new InvalidOperationException(
                "The workspace service directory has no login service (AuthService/ActionWait): "
                + "token login is impossible on this server. For On-Prem with Windows use ALTIUM_AUTH=windows.");
        }

        return _endpoints = new OAuthEndpoints(auth, wait);
    }

    /// <summary>Which scopes to request for this application. An empty list — the server does not know such a client_id.</summary>
    public async Task<IReadOnlyList<string>> GetScopesAsync(
        OAuthEndpoints endpoints, string clientId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        using HttpResponseMessage response = await _http.GetAsync(endpoints.ClientScopes(clientId), timeout.Token);
        string text = await response.Content.ReadAsStringAsync(timeout.Token);

        if (!response.IsSuccessStatusCode)
        {
            throw new OAuthRejectedException(
                $"The login server returned no scope list for the application (HTTP {(int)response.StatusCode}): {Shorten(text)}", null);
        }

        return ParseScopes(text);
    }

    /// <summary>Parses the <c>api/ClientScopes</c> response: a JSON array of strings (pure logic).</summary>
    public static IReadOnlyList<string> ParseScopes(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!)
                    .ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The link the user opens in the browser (pure logic).</summary>
    public static Uri BuildAuthorizeUrl(
        OAuthEndpoints endpoints, string clientId, IEnumerable<string> scopes, string state, string challenge)
    {
        var query = new (string Name, string Value)[]
        {
            ("client_id", clientId),
            ("response_type", "code"),
            ("scope", string.Join(' ', scopes)),
            ("redirect_uri", endpoints.AuthComplete.ToString()),
            ("code_challenge", challenge),
            ("state", state),
            ("code_challenge_method", "S256"),
        };

        return new Uri(endpoints.Authorize.AbsoluteUri + "?" + string.Join(
            "&", query.Select(pair => $"{pair.Name}={Uri.EscapeDataString(pair.Value)}")));
    }

    /// <summary>
    /// Waits until the user logs in in the browser: the long poll of the ActionWait service, the request is named
    /// by the <c>state</c> value. Returns the authorization code.
    /// </summary>
    public async Task<string> AwaitCodeAsync(OAuthEndpoints endpoints, string state, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(LoginTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoints.ActionWaitAwait)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { token = state }), Encoding.UTF8, "application/json"),
        };

        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request, timeout.Token);
            string text = await response.Content.ReadAsStringAsync(timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                throw new OAuthRejectedException(
                    $"The login wait service answered HTTP {(int)response.StatusCode}: {Shorten(text)}", null);
            }

            ActionWaitResult result = ParseActionWait(text);

            if (!string.IsNullOrEmpty(result.Error))
            {
                throw new OAuthRejectedException($"The browser login was not completed: {result.Error}.", result.Error);
            }

            return result.Code ?? throw new OAuthRejectedException(
                "The login wait service answered without an authorization code. Repeat the login (vault_session action=login).", null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OAuthRejectedException(
                $"The login was not confirmed within {LoginTimeout.TotalMinutes:0} min. Repeat: vault_session action=login.", "timeout");
        }
    }

    /// <summary>
    /// Parses the ActionWait response: <c>{ActionResult, Data:{code, error, …}}</c>; field names are case-insensitive,
    /// the fallback — fields at the root (pure logic).
    /// </summary>
    public static ActionWaitResult ParseActionWait(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            foreach (JsonElement scope in new[] { Child(root, "data") ?? root, root })
            {
                string? code = Text(scope, "code");
                string? error = Text(scope, "error");

                if (!string.IsNullOrEmpty(code) || !string.IsNullOrEmpty(error))
                {
                    string? description = Text(scope, "error_description");
                    return new ActionWaitResult(code, error is null ? null : description is null ? error : $"{error} ({description})");
                }
            }
        }
        catch (JsonException)
        {
            // The response is not JSON — "empty" is returned below, and the caller will report the missing code.
        }

        return new ActionWaitResult(null, null);
    }

    public Task<TokenSet> ExchangeCodeAsync(
        OAuthEndpoints endpoints, string clientId, string code, string verifier, CancellationToken cancellationToken) =>
        PostTokenAsync(
            endpoints,
            [
                ("grant_type", "authorization_code"),
                ("code", code),
                ("redirect_uri", endpoints.AuthComplete.ToString()),
                ("code_verifier", verifier),
                ("client_id", clientId),
            ],
            previousRefreshToken: null,
            cancellationToken);

    /// <summary>Refreshes the tokens. The server may issue a new refresh token — it must be saved, the old one stops being valid.</summary>
    public Task<TokenSet> RefreshAsync(
        OAuthEndpoints endpoints, string clientId, string refreshToken, CancellationToken cancellationToken) =>
        PostTokenAsync(
            endpoints,
            [
                ("grant_type", "refresh_token"),
                ("refresh_token", refreshToken),
                ("client_id", clientId),
            ],
            previousRefreshToken: refreshToken,
            cancellationToken);

    /// <summary>Revokes the refresh token on the server. Errors are not raised: the token will be forgotten locally anyway.</summary>
    public async Task RevokeAsync(
        OAuthEndpoints endpoints, string clientId, string refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoints.Revocation)
            {
                Content = new FormUrlEncodedContent(
                [
                    new("token", refreshToken),
                    new("token_type_hint", "refresh_token"),
                    new("client_id", clientId),
                ]),
            };
            using HttpResponseMessage response = await _http.SendAsync(request, timeout.Token);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            // The token will be deleted locally anyway.
        }
    }

    private async Task<TokenSet> PostTokenAsync(
        OAuthEndpoints endpoints,
        (string Name, string Value)[] form,
        string? previousRefreshToken,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoints.Token)
        {
            Content = new FormUrlEncodedContent(form.Select(pair => new KeyValuePair<string, string>(pair.Name, pair.Value))),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage response = await _http.SendAsync(request, timeout.Token);
        string text = await response.Content.ReadAsStringAsync(timeout.Token);

        if (!response.IsSuccessStatusCode)
        {
            (string? error, string? description) = ParseError(text);
            throw new OAuthRejectedException(
                $"The login server rejected the token request (HTTP {(int)response.StatusCode}): "
                + $"{error ?? "no error code"}{(description is null ? string.Empty : " — " + description)}.",
                error);
        }

        return ParseTokenResponse(text, DateTimeOffset.UtcNow, previousRefreshToken);
    }

    /// <summary>
    /// Parses the <c>connect/token</c> response (pure logic). The expiry — from the <c>exp</c> field of the access token,
    /// the fallback — <c>expires_in</c>. If the server did not issue a new refresh token, the old one stays.
    /// </summary>
    public static TokenSet ParseTokenResponse(string json, DateTimeOffset now, string? previousRefreshToken = null)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new OAuthRejectedException("The login server returned a response that is not JSON.", null);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            string access = Text(root, "access_token")
                ?? throw new OAuthRejectedException("The login server response has no access_token.", null);

            DateTimeOffset expires = JwtClaims.Expiry(access)
                ?? (root.TryGetProperty("expires_in", out JsonElement lifetime) && lifetime.TryGetInt64(out long seconds)
                    ? now.AddSeconds(seconds)
                    : now.AddMinutes(5));

            return new TokenSet(access, Text(root, "refresh_token") ?? previousRefreshToken, Text(root, "id_token"), expires);
        }
    }

    private static (string? Error, string? Description) ParseError(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return (Text(document.RootElement, "error"), Text(document.RootElement, "error_description"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static JsonElement? Child(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Object)
                {
                    return property.Value;
                }
            }
        }

        return null;
    }

    private static string? Text(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(property.Value.GetString()))
                {
                    return property.Value.GetString();
                }
            }
        }

        return null;
    }

    private static string Shorten(string text) => text.Length > 300 ? text[..300] + " …" : text;
}
