using System.ServiceModel;
using AltiumWorkspaceMCP.Auth;
using AltiumWorkspaceMCP.Configuration;
using AltiumWorkspaceMCP.Vault.Rest;
using AltiumWorkspaceMCP.Soap.Ids;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>Where the current session came from — important for diagnosing slot usage.</summary>
public enum SessionOrigin
{
    None,

    /// <summary>A session saved by the previous run was resumed.</summary>
    Reused,

    /// <summary>A new login was done, one more slot on the server is taken.</summary>
    Created,
}

public sealed record SessionSnapshot(
    bool IsEstablished,
    SessionOrigin Origin,
    string? UserName,
    DateTimeOffset? EstablishedAt,
    DateTimeOffset? LastUseDate,
    string? StateFile,
    TokenInfo? Token = null);

/// <summary>
/// The single per-process workspace session: login, check,
/// automatic recovery and guaranteed logout.
/// </summary>
/// <remarks>
/// The Altium server limits the number of concurrent sessions, and an abandoned session
/// stays occupied until it expires (about a month). Therefore here:
/// there is one session per process; on start a previously saved one is continued;
/// on shutdown no logout is done; an expired session is recovered
/// transparently, without creating a second one.
/// </remarks>
public sealed class VaultSession : IAsyncDisposable
{
    /// <summary>The error code by which the server reports that the session is invalid.</summary>
    internal const string UserLoginRequired = "Client.IDS.UserLoginRequired";

    private readonly VaultOptions _options;
    private readonly VaultEndpoints _endpoints;
    private readonly SessionStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TokenAuthenticator? _tokens;
    private readonly HttpClient? _authHttp;

    private string? _sessionId;
    private SessionOrigin _origin = SessionOrigin.None;
    private DateTimeOffset? _establishedAt;
    private DateTimeOffset? _lastUseDate;
    private bool _disposed;

    public VaultSession(VaultOptions options, VaultEndpoints endpoints)
        : this(options, endpoints, tokens: null)
    {
        if (options.UsesTokenAuthentication)
        {
            // The long ActionWait poll is limited by the login client itself (5 minutes), not by the HttpClient timeout.
            _authHttp = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            _tokens = new TokenAuthenticator(options, new OAuthClient(_authHttp, new ServiceDirectory(options.BaseUrl, _authHttp)));
        }
    }

    internal VaultSession(VaultOptions options, VaultEndpoints endpoints, TokenAuthenticator? tokens)
    {
        _options = options;
        _endpoints = endpoints;
        _store = new SessionStore(options.StateDirectory);
        _tokens = tokens;
    }

    /// <summary>Token login: the browser link, the login state. <c>null</c> if another login method is used.</summary>
    public TokenAuthenticator? Tokens => _tokens;

    /// <summary>Returns the identifier of the active session, logging in if needed.</summary>
    public async Task<string> AcquireAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_tokens is not null)
        {
            // The access token is the session identifier; its lifetime and refresh are handled by TokenAuthenticator.
            return await AcquireTokenAsync(cancellationToken);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _sessionId ?? await EstablishAsync(forceNewSession: false, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs a service call with the active session. If the server reports that the session
    /// has expired, one transparent re-login and one retry are done.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(
        Func<string, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        string sessionId = await AcquireAsync(cancellationToken);

        try
        {
            return await operation(sessionId, cancellationToken);
        }
        catch (Exception exception) when (IsSessionExpired(exception))
        {
            string renewed = await RenewAsync(staleSessionId: sessionId, cancellationToken);
            return await operation(renewed, cancellationToken);
        }
    }

    public async Task<SessionSnapshot> DescribeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return new SessionSnapshot(
                IsEstablished: _sessionId is not null,
                Origin: _origin,
                UserName: _options.UserName,
                EstablishedAt: _establishedAt,
                LastUseDate: _lastUseDate,
                StateFile: _tokens is null ? _store.FilePath : _tokens.StoreFile,
                Token: _tokens?.Describe());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Closes the current session and opens a new one at once. Used when the previous
    /// session hung on the server: <see cref="LoginOptions.KillExistingSession"/> frees
    /// the occupied slot.
    /// </summary>
    public async Task<string> ResetAsync(CancellationToken cancellationToken)
    {
        if (_tokens is not null)
        {
            // A new session cannot be opened here: the login goes through the browser. The tokens are forgotten, and the next
            // call reports that vault_session action=login is needed.
            await ForgetTokensAsync(cancellationToken);
            return await AcquireTokenAsync(cancellationToken);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LogoutQuietlyAsync(_sessionId, cancellationToken);
            _sessionId = null;
            return await EstablishAsync(forceNewSession: true, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Ends the session on the server and removes it from local storage.</summary>
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_tokens is not null)
        {
            await ForgetTokensAsync(cancellationToken);
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LogoutQuietlyAsync(_sessionId, cancellationToken);
            _sessionId = null;
            _origin = SessionOrigin.None;
            _establishedAt = null;
            _store.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Process shutdown intentionally does NOT close the session: the identifier stays
    /// in storage, and the next run continues it instead of a new login.
    /// </summary>
    /// <remarks>
    /// This is the main rule of working with slots. A session lives about a month, and the number
    /// of concurrent sessions is limited by the license. If the session were closed on every
    /// exit, any client restart would spend a new slot, and a session abandoned on a crash
    /// would stay occupied until it expires. One long-lived reused
    /// session holds exactly one slot regardless of the number of runs.
    /// It can be closed explicitly: the <c>logout</c> command or the vault_session tool.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _gate.Dispose();
        _tokens?.Dispose();
        _authHttp?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Logs in to the workspace. First tries to continue the saved session,
    /// so as not to take an extra slot.
    /// </summary>
    private async Task<string> EstablishAsync(bool forceNewSession, CancellationToken cancellationToken)
    {
        if (!forceNewSession)
        {
            string? reused = await TryReusePersistedSessionAsync(cancellationToken);
            if (reused is not null)
            {
                _sessionId = reused;
                _origin = SessionOrigin.Reused;
                return reused;
            }
        }

        // KillExistingSession frees the slot left by an abnormally terminated process.
        LoginOptions loginOptions = forceNewSession ? LoginOptions.KillExistingSession : LoginOptions.None;
        var login = await LoginAsync(loginOptions, cancellationToken);

        if (string.IsNullOrWhiteSpace(login.SessionId))
        {
            throw new InvalidOperationException(
                "Login to the Altium workspace succeeded, but the server did not return a session identifier.");
        }

        _sessionId = login.SessionId;
        _origin = SessionOrigin.Created;
        _establishedAt = DateTimeOffset.UtcNow;
        _lastUseDate = login.LastUseDate == default ? null : new DateTimeOffset(login.LastUseDate, TimeSpan.Zero);

        _store.Save(new PersistedSession
        {
            SessionId = login.SessionId,
            BaseUrl = _options.BaseUrl.ToString(),
            UserName = _options.UserName,
            EstablishedAt = _establishedAt.Value,
        });

        return login.SessionId;
    }

    /// <summary>
    /// Checks the saved session on the server. Returns it if it is still alive
    /// and was issued to the same server and user.
    /// </summary>
    private async Task<string?> TryReusePersistedSessionAsync(CancellationToken cancellationToken)
    {
        PersistedSession? persisted = _store.Load();
        if (persisted is null)
        {
            return null;
        }

        bool sameTarget =
            Uri.TryCreate(persisted.BaseUrl, UriKind.Absolute, out Uri? storedUrl)
            && storedUrl == _options.BaseUrl
            && string.Equals(persisted.UserName, _options.UserName, StringComparison.OrdinalIgnoreCase);

        if (!sameTarget)
        {
            _store.Clear();
            return null;
        }

        var client = _endpoints.CreateIdsClient();
        try
        {
            IDS_LoginResult? info = await client.GetSessionInfoAsync(VaultEndpoints.ApiVersion, persisted.SessionId);
            if (string.IsNullOrWhiteSpace(info?.SessionId))
            {
                _store.Clear();
                return null;
            }

            _establishedAt = persisted.EstablishedAt;
            DateTime lastUse = info.LastUseDate;
            _lastUseDate = lastUse == default ? null : new DateTimeOffset(lastUse, TimeSpan.Zero);
            return persisted.SessionId;
        }
        catch (Exception exception) when (exception is CommunicationException or TimeoutException)
        {
            // The session is revoked, expired or the server is unavailable — create a new one.
            _store.Clear();
            return null;
        }
        finally
        {
            await CloseClientAsync(client);
        }
    }

    private async Task<IDS_LoginResult> LoginAsync(LoginOptions loginOptions, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IDS_LoginResult? result;

        if (_options.UsesWindowsAuthentication)
        {
            var ntlm = _endpoints.CreateNtlmClient();
            try
            {
                // User name and password are empty: the server takes the identity from the NTLM negotiation.
                result = await ntlm.LoginAsync(VaultEndpoints.ApiVersion, string.Empty, string.Empty, false, loginOptions);
            }
            finally
            {
                await CloseClientAsync(ntlm);
            }
        }
        else
        {
            var ids = _endpoints.CreateIdsClient();
            try
            {
                result = await ids.LoginAsync(
                    VaultEndpoints.ApiVersion,
                    _options.UserName ?? string.Empty,
                    _options.Password ?? string.Empty,
                    false,
                    loginOptions);
            }
            finally
            {
                await CloseClientAsync(ids);
            }
        }

        return result ?? throw new InvalidOperationException(
            "The server accepted the login but returned no result (an empty response from the login service).");
    }

    private async Task<string> AcquireTokenAsync(CancellationToken cancellationToken)
    {
        string token = await _tokens!.GetAccessTokenAsync(cancellationToken);

        if (_sessionId is null)
        {
            _origin = SessionOrigin.Created;
            _establishedAt = DateTimeOffset.UtcNow;
        }

        _sessionId = token;
        return token;
    }

    private async Task ForgetTokensAsync(CancellationToken cancellationToken)
    {
        await _tokens!.ForgetAsync(cancellationToken);
        _sessionId = null;
        _origin = SessionOrigin.None;
        _establishedAt = null;
    }

    /// <summary>Logs out without raising an error: the slot will be freed by expiry anyway.</summary>
    private async Task LogoutQuietlyAsync(string? sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var client = _endpoints.CreateIdsClient();
        try
        {
            await client.LogoutAsync(VaultEndpoints.ApiVersion, sessionId, string.Empty)
                .WaitAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is CommunicationException or TimeoutException or OperationCanceledException)
        {
            // The session is already invalid or the server is unavailable.
        }
        finally
        {
            await CloseClientAsync(client);
        }
    }

    /// <summary>Recovers the session after the server rejected the previous one.</summary>
    private async Task<string> RenewAsync(string staleSessionId, CancellationToken cancellationToken)
    {
        if (_tokens is not null)
        {
            // The server rejected the token: one refresh by the refresh token; if that fails — a browser login is needed.
            TokenSet renewed = await _tokens.RefreshAsync(staleSessionId, cancellationToken);
            _sessionId = renewed.AccessToken;
            return renewed.AccessToken;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // While we waited in the queue, another call may have already logged in again.
            if (_sessionId is not null && _sessionId != staleSessionId)
            {
                return _sessionId;
            }

            _sessionId = null;
            _store.Clear();
            return await EstablishAsync(forceNewSession: false, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A session is considered expired by the server error code. Other connection failures
    /// must not lead to a re-login and spending one more slot.
    /// </summary>
    internal static bool IsSessionExpired(Exception exception) => exception switch
    {
        VaultOperationException vault =>
            string.Equals(vault.FaultCode, UserLoginRequired, StringComparison.OrdinalIgnoreCase),
        FaultException fault =>
            fault.Message.Contains(UserLoginRequired, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>Closes the WCF channel, not letting a close error mask the original one.</summary>
    private static async Task CloseClientAsync(ICommunicationObject client)
    {
        try
        {
            if (client.State == CommunicationState.Faulted)
            {
                client.Abort();
                return;
            }

            await Task.Factory.FromAsync(client.BeginClose, client.EndClose, null);
        }
        catch (Exception exception) when (exception is CommunicationException or TimeoutException)
        {
            client.Abort();
        }
    }
}
