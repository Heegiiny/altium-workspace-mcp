using System.ComponentModel;
using System.Text.Json;
using AltiumWorkspaceMCP.Auth;
using AltiumWorkspaceMCP.Vault;
using ModelContextProtocol.Server;

namespace AltiumWorkspaceMCP.Tools;

/// <summary>Vault session management and the change log view.</summary>
[McpServerToolType]
public sealed class SessionTools
{
    private readonly VaultWorkspace _workspace;

    public SessionTools(VaultWorkspace workspace) => _workspace = workspace;

    [McpServerTool(Name = "vault_session")]
    [Description("""
        Workspace session management. The number of concurrent sessions on the server is
        limited by the license, so the server keeps one session and reuses it between
        runs instead of opening a new one every time.

        Actions:
          show  — state of the current session (default); status — the same;
          reset — close the current session and open a new one, freeing a stuck slot on the server;
          close — close the session and free the slot; the next request logs in again.

        Token login (ALTIUM_AUTH=token, the default outside Windows). The agent needs no password and
        none is accepted: the user logs in in the browser. Order:
          login        — start the login: returns loginUrl; SHOW the link to the user so they open
                         it and log in with their own account (waits up to 5 minutes);
          login_status — wait for the login (up to waitSeconds) and get the result; repeat
                         while state = pending;
          reset        — forget the tokens and start the login again (returns a new link);
          close        — forget the tokens (the refresh token is revoked on the server).
        If any other tool answers "login required", call login.
        """)]
    public async Task<object> ManageSessionAsync(
        [Description("show (also status), reset, close, login or login_status.")]
        string action = "show",
        [Description("Only for login_status: how many seconds to wait for the browser login (0–60).")]
        int waitSeconds = 20,
        CancellationToken cancellationToken = default)
    {
        TokenAuthenticator? tokens = _workspace.Session.Tokens;

        switch (NormalizeAction(action))
        {
            case "show":
                break;

            case "login":
                return DescribeLogin(await RequireTokens(tokens).StartLoginAsync(cancellationToken), tokens!);

            case "login_status":
                return DescribeLogin(
                    await RequireTokens(tokens).GetLoginStatusAsync(
                        TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0, 60)), cancellationToken),
                    tokens!);

            case "reset" when tokens is not null:
                await _workspace.Session.CloseAsync(cancellationToken);
                return DescribeLogin(await tokens.StartLoginAsync(cancellationToken), tokens);

            case "reset":
                await _workspace.Session.ResetAsync(cancellationToken);
                break;

            case "close":
                await _workspace.Session.CloseAsync(cancellationToken);
                return new
                {
                    closed = true,
                    note = tokens is null
                        ? "Session closed, the server slot is freed. The next request logs in again."
                        : "Tokens forgotten (the refresh token is revoked on the server, the token file is deleted). "
                            + "To keep working, run vault_session action=login.",
                };

            default:
                throw new ArgumentException(
                    $"Unknown action '{action}'. Allowed: show, status, reset, close, login, login_status.", nameof(action));
        }

        SessionSnapshot snapshot = await _workspace.Session.DescribeAsync(cancellationToken);

        if (tokens is not null)
        {
            TokenInfo info = snapshot.Token ?? tokens.Describe();
            return new
            {
                method = "token",
                established = info.HasTokens,
                account = info.Account,
                tokenExpiresAt = info.ExpiresAt,
                canRefresh = info.HasRefreshToken,
                loginPending = info.LoginPending,
                tokenStore = snapshot.StateFile ?? "process memory (ALTIUM_TOKEN_STORE=file saves to a file)",
                next = info.HasTokens ? null : "Not logged in: call vault_session action=login.",
            };
        }

        return new
        {
            established = snapshot.IsEstablished,
            origin = snapshot.Origin switch
            {
                SessionOrigin.Reused => "saved session resumed",
                SessionOrigin.Created => "new session opened",
                _ => "session not opened yet",
            },
            account = _workspace.Options.UsesWindowsAuthentication
                ? "Windows account"
                : snapshot.UserName,
            establishedAt = snapshot.EstablishedAt,
            lastUseDate = snapshot.LastUseDate,
            stateFile = snapshot.StateFile,
        };
    }

    /// <summary>Action without case and spaces; <c>status</c> is a synonym of <c>show</c> (agents ask exactly that way).</summary>
    internal static string NormalizeAction(string? action)
    {
        string normalized = (action ?? string.Empty).Trim().ToLowerInvariant();

        return normalized == "status" ? "show" : normalized;
    }

    private TokenAuthenticator RequireTokens(TokenAuthenticator? tokens) =>
        tokens ?? throw new ArgumentException(
            "The login and login_status actions are only for token login (ALTIUM_AUTH=token). "
            + "Currently the login is done "
            + (_workspace.Options.UsesWindowsAuthentication ? "with the Windows account." : "with the user name and password from the environment."),
            "action");

    private static object DescribeLogin(LoginStatus status, TokenAuthenticator tokens)
    {
        TokenInfo info = tokens.Describe();

        return status.State switch
        {
            LoginState.Pending => new
            {
                state = "pending",
                loginUrl = status.Url?.AbsoluteUri,
                expiresAt = status.ExpiresAt,
                next = "Show loginUrl to the user: they open the link in the browser and log in with their own account. "
                    + "Then call vault_session action=login_status until state becomes completed.",
            },
            LoginState.Completed => (object)new
            {
                state = "completed",
                account = info.Account,
                tokenExpiresAt = info.ExpiresAt,
                canRefresh = info.HasRefreshToken,
                next = "Logged in, you can work with the vault.",
            },
            LoginState.Failed => new
            {
                state = "failed",
                error = status.Error,
                next = "Login failed. Fix the cause and call vault_session action=login again.",
            },
            _ => new
            {
                state = "none",
                next = "Login not started. Call vault_session action=login.",
            },
        };
    }

    [McpServerTool(Name = "vault_audit", ReadOnly = true)]
    [Description("""
        Latest change log entries: what was changed, how it ended and what the
        values were before the edit. These entries allow restoring the previous state manually.

        An entry charged to a plan (vault_plan) carries its number in the plan field —
        the plan parameter narrows the log to the entries of one plan.
        """)]
    public async Task<object> ReadAuditAsync(
        [Description("How many entries to show per call.")]
        int count = 20,
        [Description("How many of the newest entries to skip: take nextOffset from the previous response to read deeper.")]
        int offset = 0,
        [Description("Show only the entries of this plan (number from vault_plan create/show).")]
        int? plan = null,
        CancellationToken cancellationToken = default)
    {
        offset = Math.Max(offset, 0);

        // One more entry is read: this tells whether there is anything further to page.
        // Filtering by plan runs over the whole log: a plan is a small part of the entries and,
        // if offset/count were applied first, the needed entries would be easy to lose.
        var all = await _workspace.Audit.ReadRecentAsync(int.MaxValue, cancellationToken);
        var filtered = plan is null ? all : all.Where(line => MatchesPlan(line, plan.Value)).ToList();
        var read = filtered.Take(offset + count + 1).ToList();
        var page = read.Skip(offset).ToList();

        // One entry for editing hundreds of components can be long: long ones are cut, the full
        // text stays in the log file.
        var budget = new ResponseBudget(_workspace.Options.MaxResponseChars, reserved: 500);
        var entries = new List<string>();

        foreach (string line in page.Take(count))
        {
            string shown = line.Length <= MaxEntryChars
                ? line
                : line[..MaxEntryChars] + $" …[truncated, {line.Length} characters; full text is in the log file]";

            if (!budget.TryAdd(shown))
            {
                break;
            }

            entries.Add(shown);
        }

        bool more = entries.Count < page.Count;

        return new
        {
            file = _workspace.Audit.FilePath,
            count = entries.Count,
            nextOffset = more ? offset + entries.Count : (int?)null,
            entries,
        };
    }

    /// <summary>Length of one log entry in the response.</summary>
    private const int MaxEntryChars = 4000;

    private static bool MatchesPlan(string line, int plan)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            return document.RootElement.TryGetProperty("plan", out JsonElement value)
                && value.ValueKind == JsonValueKind.Number
                && value.GetInt32() == plan;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
