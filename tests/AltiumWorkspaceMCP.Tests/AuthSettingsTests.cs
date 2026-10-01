using AltiumWorkspaceMCP.Auth;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>Choosing the login method and the token storage place.</summary>
public sealed class AuthSettingsTests
{
    [Theory]
    [InlineData(null, null, true, AuthMethod.Windows)]
    [InlineData(null, null, false, AuthMethod.Token)]
    [InlineData("", "", false, AuthMethod.Token)]
    [InlineData("token", null, true, AuthMethod.Token)]
    [InlineData("TOKEN", "operator", true, AuthMethod.Token)]
    [InlineData("windows", null, true, AuthMethod.Windows)]
    [InlineData(null, "operator", false, AuthMethod.Windows)]
    [InlineData("windows", "operator", false, AuthMethod.Windows)]
    public void AuthMethodIsChosenBySystemAndVariables(string? auth, string? user, bool isWindows, AuthMethod expected)
    {
        Assert.Equal(expected, AuthSettings.ResolveMethod(auth, user, isWindows));
    }

    [Fact]
    public void NtlmOutsideWindowsIsRefusedWithTokenHint()
    {
        var error = Assert.Throws<InvalidOperationException>(() => AuthSettings.ResolveMethod("windows", null, isWindows: false));

        Assert.Contains("only on Windows", error.Message);
        Assert.Contains("ALTIUM_AUTH=token", error.Message);
    }

    [Fact]
    public void UnknownAuthMethodIsRefusedWithAllowedList()
    {
        var error = Assert.Throws<InvalidOperationException>(() => AuthSettings.ResolveMethod("kerberos", null, true));

        Assert.Contains("windows, token", error.Message);
    }

    [Theory]
    [InlineData(null, TokenStoreMode.Memory)]
    [InlineData("memory", TokenStoreMode.Memory)]
    [InlineData("FILE", TokenStoreMode.File)]
    public void TokenStoreDefaultsToMemory(string? value, TokenStoreMode expected)
    {
        Assert.Equal(expected, AuthSettings.ResolveTokenStore(value));
    }

    [Fact]
    public void InvalidTokenStoreIsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => AuthSettings.ResolveTokenStore("registry"));
    }
}
