using AltiumWorkspaceMCP.Configuration;

namespace AltiumWorkspaceMCP.Auth;

/// <summary>A browser login (or a repeated login) is required: the text tells the agent what to call.</summary>
public sealed class LoginRequiredException(string message) : InvalidOperationException(message);

public enum LoginState
{
    /// <summary>The login has not started.</summary>
    None,

    /// <summary>The link was issued, the server waits for the user to log in in the browser.</summary>
    Pending,

    /// <summary>Logged in, the tokens are received.</summary>
    Completed,

    /// <summary>The login failed or was cancelled; the reason is in <see cref="LoginStatus.Error"/>.</summary>
    Failed,
}

/// <param name="Url">The login link (while the login waits for confirmation).</param>
/// <param name="ExpiresAt">Until when the link waits for the login.</param>
public sealed record LoginStatus(LoginState State, Uri? Url, DateTimeOffset? ExpiresAt, string? Error);

/// <summary>What is known about the tokens (without the tokens themselves).</summary>
public sealed record TokenInfo(
    bool HasTokens, DateTimeOffset? ExpiresAt, bool HasRefreshToken, string? Account, bool LoginPending);

/// <summary>
/// Token login: the browser link, waiting for the login, storing and refreshing the tokens. The access token
/// serves as the workspace session identifier (<see cref="Vault.VaultSession"/> passes it
/// where an NTLM session passes its identifier).
/// </summary>
/// <remarks>
/// The password is not accepted or stored: the user enters it on the login server page.
/// The tokens live in process memory; they get into a file only with <c>ALTIUM_TOKEN_STORE=file</c>.
/// </remarks>
public sealed class TokenAuthenticator : IDisposable
{
    /// <summary>How long before expiry the token is refreshed.</summary>
    public static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(60);

    private readonly VaultOptions _options;
    private readonly OAuthClient _client;
    private readonly TokenStore? _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private TokenSet? _tokens;
    private bool _loaded;
    private LoginAttempt? _attempt;
    private string? _lastError;

    private sealed record LoginAttempt(Uri Url, DateTimeOffset ExpiresAt, Task<TokenSet> Task, CancellationTokenSource Cancel);

    public TokenAuthenticator(VaultOptions options, OAuthClient client, Func<DateTimeOffset>? clock = null)
    {
        _options = options;
        _client = client;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _store = options.TokenStore == TokenStoreMode.File ? new TokenStore(options.StateDirectory) : null;
    }

    /// <summary>The token file; <c>null</c> if the tokens are kept in memory only.</summary>
    public string? StoreFile => _store?.FilePath;

    public TokenInfo Describe()
    {
        EnsureLoaded();

        lock (_sync)
        {
            return new TokenInfo(
                _tokens is not null,
                _tokens?.ExpiresAt,
                _tokens?.RefreshToken is not null,
                _tokens?.Account,
                _attempt is { Task.IsCompleted: false });
        }
    }

    /// <summary>
    /// Starts the login: returns the link the user opens in the browser, while waiting for their
    /// login goes on in the background (up to 5 minutes). A repeated call while the login waits returns the same link.
    /// </summary>
    public async Task<LoginStatus> StartLoginAsync(CancellationToken cancellationToken)
    {
        string clientId = RequireClientId();

        lock (_sync)
        {
            if (_attempt is { Task.IsCompleted: false } running)
            {
                return new LoginStatus(LoginState.Pending, running.Url, running.ExpiresAt, null);
            }
        }

        OAuthEndpoints endpoints = await _client.ResolveAsync(cancellationToken);
        IReadOnlyList<string> scopes = await _client.GetScopesAsync(endpoints, clientId, cancellationToken);

        if (scopes.Count == 0)
        {
            throw new OAuthRejectedException(
                $"The login server does not know an application with the client_id from ALTIUM_OAUTH_CLIENT_ID (the scope list is empty) at {endpoints.AuthService}. "
                + "Check the identifier: it must be registered on this login server.",
                "unauthorized_client");
        }

        string verifier = Pkce.CreateVerifier();
        string state = Pkce.CreateState();
        Uri url = OAuthClient.BuildAuthorizeUrl(endpoints, clientId, scopes, state, Pkce.CreateChallenge(verifier));

        var cancel = new CancellationTokenSource();
        Task<TokenSet> task = Task.Run(() => CompleteLoginAsync(endpoints, clientId, verifier, state, cancel.Token), CancellationToken.None);
        var attempt = new LoginAttempt(url, _clock() + OAuthClient.LoginTimeout, task, cancel);

        lock (_sync)
        {
            if (_attempt is { Task.IsCompleted: false } concurrent)
            {
                cancel.Cancel();
                return new LoginStatus(LoginState.Pending, concurrent.Url, concurrent.ExpiresAt, null);
            }

            _attempt = attempt;
            _lastError = null;
        }

        return new LoginStatus(LoginState.Pending, url, attempt.ExpiresAt, null);
    }

    /// <summary>
    /// The login state. If it still waits for the browser, waits no longer than <paramref name="wait"/>
    /// and returns earlier as soon as the login completes.
    /// </summary>
    public async Task<LoginStatus> GetLoginStatusAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        EnsureLoaded();

        LoginAttempt? attempt;
        lock (_sync)
        {
            attempt = _attempt;
        }

        if (attempt is { Task.IsCompleted: false } && wait > TimeSpan.Zero)
        {
            await Task.WhenAny(attempt.Task, Task.Delay(wait, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
        }

        lock (_sync)
        {
            if (_attempt is { } current && current.Task.IsCompleted)
            {
                if (current.Task.IsFaulted || current.Task.IsCanceled)
                {
                    _lastError = DescribeFailure(current.Task);
                }

                _attempt = null;
                current.Cancel.Dispose();
            }

            if (_attempt is { } pending)
            {
                return new LoginStatus(LoginState.Pending, pending.Url, pending.ExpiresAt, null);
            }

            if (_lastError is not null)
            {
                return new LoginStatus(LoginState.Failed, null, null, _lastError);
            }

            return new LoginStatus(_tokens is null ? LoginState.None : LoginState.Completed, null, null, null);
        }
    }

    /// <summary>The active access token (also the session identifier); refreshes it if needed.</summary>
    /// <exception cref="LoginRequiredException">There are no tokens or they could not be refreshed: a browser login is needed.</exception>
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        EnsureLoaded();

        TokenSet? tokens = Current();
        if (tokens is null)
        {
            RequireClientId();
            throw LoginRequired("Not logged in to the workspace.");
        }

        if (!tokens.NeedsRefresh(_clock(), RefreshSkew))
        {
            return tokens.AccessToken;
        }

        return (await RefreshAsync(tokens.AccessToken, cancellationToken)).AccessToken;
    }

    /// <summary>
    /// Refreshes the tokens by the refresh token. <paramref name="staleAccessToken"/> — the token that did not fit:
    /// if another call or process has already got a new one, it is taken instead of making another request.
    /// </summary>
    public async Task<TokenSet> RefreshAsync(string staleAccessToken, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            TokenSet current = Current() ?? throw LoginRequired("Not logged in to the workspace.");

            if (current.AccessToken != staleAccessToken && !current.NeedsRefresh(_clock(), RefreshSkew))
            {
                return current;
            }

            // Another process with the same token file may have already refreshed them: the refresh token is single-use,
            // and a repeated request with the old one would be rejected.
            if (_store is not null && _options.OAuthClientId is { } id
                && _store.Load(_options.BaseUrl, id) is { } shared
                && shared.AccessToken != staleAccessToken
                && !shared.NeedsRefresh(_clock(), RefreshSkew))
            {
                lock (_sync)
                {
                    _tokens = shared;
                }

                return shared;
            }

            if (current.RefreshToken is null)
            {
                Drop();
                throw LoginRequired("The token has expired and there is no refresh token.");
            }

            string clientId = RequireClientId();
            OAuthEndpoints endpoints = await _client.ResolveAsync(cancellationToken);

            try
            {
                TokenSet renewed = await _client.RefreshAsync(endpoints, clientId, current.RefreshToken, cancellationToken);
                Accept(renewed);
                return renewed;
            }
            catch (OAuthRejectedException rejected)
            {
                Drop();
                throw LoginRequired($"The server did not accept the token refresh ({rejected.Message}).");
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Forgets the tokens: revokes the refresh token on the server (if possible), cancels a pending login, erases the file.</summary>
    public async Task ForgetAsync(CancellationToken cancellationToken)
    {
        EnsureLoaded();
        TokenSet? tokens = Current();

        if (tokens?.RefreshToken is { } refresh && _options.OAuthClientId is { } clientId)
        {
            try
            {
                await _client.RevokeAsync(await _client.ResolveAsync(cancellationToken), clientId, refresh, cancellationToken);
            }
            catch (Exception e) when (e is InvalidOperationException or HttpRequestException)
            {
                // The token will be forgotten locally anyway.
            }
        }

        lock (_sync)
        {
            _attempt?.Cancel.Cancel();
            _attempt = null;
            _lastError = null;
        }

        Drop();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _attempt?.Cancel.Cancel();
        }

        _refreshGate.Dispose();
    }

    private async Task<TokenSet> CompleteLoginAsync(
        OAuthEndpoints endpoints, string clientId, string verifier, string state, CancellationToken cancellationToken)
    {
        string code = await _client.AwaitCodeAsync(endpoints, state, cancellationToken);
        TokenSet tokens = await _client.ExchangeCodeAsync(endpoints, clientId, code, verifier, cancellationToken);
        Accept(tokens);
        return tokens;
    }

    private void Accept(TokenSet tokens)
    {
        lock (_sync)
        {
            _tokens = tokens;
            _loaded = true;
        }

        if (_store is not null && _options.OAuthClientId is { } clientId)
        {
            _store.Save(_options.BaseUrl, clientId, tokens);
        }
    }

    private void Drop()
    {
        lock (_sync)
        {
            _tokens = null;
        }

        _store?.Clear();
    }

    private TokenSet? Current()
    {
        lock (_sync)
        {
            return _tokens;
        }
    }

    /// <summary>Picks up the tokens from the file once (only with <c>ALTIUM_TOKEN_STORE=file</c>).</summary>
    private void EnsureLoaded()
    {
        lock (_sync)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;

            if (_store is not null && _options.OAuthClientId is { } clientId)
            {
                _tokens = _store.Load(_options.BaseUrl, clientId);
            }
        }
    }

    private string RequireClientId() =>
        _options.OAuthClientId
        ?? throw new InvalidOperationException(
            "Token login requires ALTIUM_OAUTH_CLIENT_ID — the identifier of the application (client_id) that the Altium login server "
            + "allows to log in through the browser. It is issued and registered by the login server (for Altium 365 — together with "
            + "the scopes and redirect_uri); the server does not guess it. Set the environment variable and restart the server.");

    private LoginRequiredException LoginRequired(string reason)
    {
        LoginAttempt? attempt;
        lock (_sync)
        {
            attempt = _attempt is { Task.IsCompleted: false } running ? running : null;
        }

        return new LoginRequiredException(
            attempt is null
                ? $"{reason} Call vault_session action=login: the tool returns a link "
                    + "that must be shown to the user (they log in in the browser with their own account), "
                    + "then vault_session action=login_status to wait for the login."
                : $"{reason} A login is already waiting for confirmation in the browser: have the user open the link {attempt.Url}, "
                    + "then call vault_session action=login_status.");
    }

    private static string DescribeFailure(Task<TokenSet> task)
    {
        Exception? error = task.Exception?.GetBaseException();
        return error switch
        {
            null => "Login cancelled.",
            _ => error.Message,
        };
    }
}
