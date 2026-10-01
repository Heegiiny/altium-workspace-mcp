using AltiumWorkspaceMCP.Auth;
using AltiumWorkspaceMCP.Configuration;
using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Diagnostics;

/// <summary>
/// Token login from the command line: prints the link, waits for the browser login and saves the tokens
/// to a file. Needed where there is no MCP client, to log in once and then run
/// the server with <c>ALTIUM_TOKEN_STORE=file</c> without opening a browser.
/// </summary>
public static class LoginCommand
{
    public static async Task<int> RunAsync(VaultOptions options)
    {
        if (!options.UsesTokenAuthentication)
        {
            Console.Error.WriteLine(
                "The login command is for token login. Set ALTIUM_AUTH=token (the default outside Windows).");
            return 2;
        }

        if (options.TokenStore != TokenStoreMode.File)
        {
            Console.Error.WriteLine(
                "Tokens obtained by a separate command cannot be handed to the server if they live only in memory. "
                + "Set ALTIUM_TOKEN_STORE=file (the token file is in the state directory) and repeat. "
                + "For a server that requests the login itself the command is not needed: use vault_session action=login.");
            return 2;
        }

        await using var session = new VaultSession(options, new VaultEndpoints(options));
        TokenAuthenticator tokens = session.Tokens!;

        LoginStatus status = await tokens.StartLoginAsync(CancellationToken.None);
        Console.Error.WriteLine("Open the link in the browser and log in with your own account:");
        Console.Error.WriteLine();
        Console.Error.WriteLine(status.Url?.AbsoluteUri);
        Console.Error.WriteLine();
        Console.Error.WriteLine($"Waiting for the login until {status.ExpiresAt:HH:mm:ss} (5 minutes)…");

        while (status.State == LoginState.Pending)
        {
            status = await tokens.GetLoginStatusAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }

        if (status.State != LoginState.Completed)
        {
            Console.Error.WriteLine($"Login failed: {status.Error}");
            return 1;
        }

        TokenInfo info = tokens.Describe();
        Console.Error.WriteLine(
            $"Logged in: {info.Account ?? "account not determined"}; the token is valid until {info.ExpiresAt:yyyy-MM-dd HH:mm:ss} UTC, "
            + $"refresh {(info.HasRefreshToken ? "possible" : "impossible")}.");
        Console.Error.WriteLine($"Tokens saved: {tokens.StoreFile}");
        return 0;
    }
}
