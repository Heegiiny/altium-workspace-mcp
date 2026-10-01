namespace AltiumWorkspaceMCP.Auth;

/// <summary>The way to log in to the workspace (<c>ALTIUM_AUTH</c>).</summary>
public enum AuthMethod
{
    /// <summary>
    /// The Windows account (NTLM) — Windows only, the default there. If
    /// <c>ALTIUM_USERNAME</c> is set, the login is by the user name and password from the environment (the earlier behavior).
    /// </summary>
    Windows,

    /// <summary>A browser login (OAuth2 + PKCE), as in Altium Designer when logging in to Altium 365; the default outside Windows.</summary>
    Token,
}

/// <summary>Where the tokens are kept (<c>ALTIUM_TOKEN_STORE</c>).</summary>
public enum TokenStoreMode
{
    /// <summary>In process memory only: a new login is needed after a restart. The default.</summary>
    Memory,

    /// <summary>In a file of the state directory: Windows — DPAPI, otherwise permissions 600.</summary>
    File,
}

/// <summary>Choosing the login method and the token storage place from environment variables (pure logic).</summary>
public static class AuthSettings
{
    /// <summary>
    /// <paramref name="auth"/> — the value of <c>ALTIUM_AUTH</c>; empty — Windows where it exists, otherwise a token.
    /// A set <c>ALTIUM_USERNAME</c> without an explicit <c>token</c> keeps the earlier login by user name.
    /// </summary>
    public static AuthMethod ResolveMethod(string? auth, string? userName, bool isWindows)
    {
        bool hasUserName = !string.IsNullOrWhiteSpace(userName);

        switch (auth?.Trim().ToLowerInvariant())
        {
            case null or "":
                return hasUserName || isWindows ? AuthMethod.Windows : AuthMethod.Token;

            case "token":
                return AuthMethod.Token;

            case "windows":
                if (!isWindows && !hasUserName)
                {
                    throw new InvalidOperationException(
                        "ALTIUM_AUTH=windows (NTLM) works only on Windows. "
                        + "Here use the token login through the browser: ALTIUM_AUTH=token and ALTIUM_OAUTH_CLIENT_ID.");
                }

                return AuthMethod.Windows;

            default:
                throw new InvalidOperationException(
                    $"ALTIUM_AUTH has an invalid value '{auth}'. Allowed: windows, token.");
        }
    }

    public static TokenStoreMode ResolveTokenStore(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "memory" => TokenStoreMode.Memory,
        "file" => TokenStoreMode.File,
        _ => throw new InvalidOperationException(
            $"ALTIUM_TOKEN_STORE has an invalid value '{value}'. Allowed: memory (default), file."),
    };
}
