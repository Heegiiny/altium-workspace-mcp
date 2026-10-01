using System.Text.Json;
using AltiumWorkspaceMCP.Auth;
using AltiumWorkspaceMCP.Configuration;
using AltiumWorkspaceMCP.Tools;
using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>Actions of the vault_session tool: status is a synonym of show.</summary>
public sealed class SessionToolsTests
{
    [Theory]
    [InlineData("show", "show")]
    [InlineData("status", "show")]
    [InlineData("  Status ", "show")]
    [InlineData("SHOW", "show")]
    [InlineData("reset", "reset")]
    [InlineData("close", "close")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ActionIsNormalized(string? action, string expected) =>
        Assert.Equal(expected, SessionTools.NormalizeAction(action));

    [Fact]
    public void UnknownActionIsNotAliased() =>
        Assert.Equal("logout", SessionTools.NormalizeAction("logout"));

    private static VaultOptions Options(AuthMethod method, string? userName = null) => new()
    {
        BaseUrl = new Uri("http://vault.example.local:9780"),
        AuthMethod = method,
        UserName = userName,
        StateDirectory = "state",
        ExchangeDirectory = "exchange",
    };

    private static JsonElement SignIn(VaultOptions options, SessionSnapshot snapshot) =>
        JsonSerializer.SerializeToElement(ExplorerTools.DescribeSignIn(options, snapshot));

    [Fact]
    public void StatusInTokenModeShowsSignInMethodNotSessionOrigin()
    {
        var token = new TokenInfo(HasTokens: false, ExpiresAt: null, HasRefreshToken: false, Account: null, LoginPending: true);
        var snapshot = new SessionSnapshot(false, SessionOrigin.None, null, null, null, null, token);

        JsonElement signIn = SignIn(Options(AuthMethod.Token), snapshot);

        Assert.Equal("token (browser login)", signIn.GetProperty("method").GetString());
        Assert.False(signIn.GetProperty("loggedIn").GetBoolean());
        Assert.True(signIn.GetProperty("loginPending").GetBoolean());
        Assert.False(signIn.TryGetProperty("origin", out _));
    }

    [Fact]
    public void StatusInTokenModeShowsTokenLifetime()
    {
        var expires = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var token = new TokenInfo(HasTokens: true, ExpiresAt: expires, HasRefreshToken: true, Account: "user", LoginPending: false);
        var snapshot = new SessionSnapshot(true, SessionOrigin.None, null, null, null, null, token);

        JsonElement signIn = SignIn(Options(AuthMethod.Token), snapshot);

        Assert.True(signIn.GetProperty("loggedIn").GetBoolean());
        Assert.True(signIn.GetProperty("canRefresh").GetBoolean());
        Assert.Equal(expires, signIn.GetProperty("tokenExpiresAt").GetDateTimeOffset());
    }

    [Theory]
    [InlineData(null, "windows (NTLM)")]
    [InlineData("user", "user name and password")]
    public void StatusInOtherModesKeepsSessionOrigin(string? userName, string method)
    {
        var snapshot = new SessionSnapshot(true, SessionOrigin.Reused, userName, null, null, null);

        JsonElement signIn = SignIn(Options(AuthMethod.Windows, userName), snapshot);

        Assert.Equal(method, signIn.GetProperty("method").GetString());
        Assert.True(signIn.GetProperty("loggedIn").GetBoolean());
        Assert.Equal("saved session resumed", signIn.GetProperty("origin").GetString());
    }
}
