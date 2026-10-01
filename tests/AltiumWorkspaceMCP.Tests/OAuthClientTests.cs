using System.Text;
using AltiumWorkspaceMCP.Auth;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>Pure token login logic: PKCE, addresses, the browser link, parsing login service responses.</summary>
public sealed class OAuthClientTests
{
    private static readonly OAuthEndpoints Endpoints = new(
        new Uri("http://srv:9780/unifiedlogin"), new Uri("http://srv:9780/actionwait/await"));

    [Fact]
    public void PkceGivesRfc7636Vector()
    {
        // RFC 7636, Appendix B.
        Assert.Equal(
            "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            Pkce.CreateChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void VerifierIsLikeDesigner54BytesInBase64Url()
    {
        string verifier = Pkce.CreateVerifier();

        Assert.Equal(72, verifier.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", verifier);
        Assert.NotEqual(verifier, Pkce.CreateVerifier());
        Assert.True(Guid.TryParse(Pkce.CreateState(), out _));
    }

    [Fact]
    public void LoginServiceAddressesAreBuiltFromAuthServiceAddress()
    {
        Assert.Equal("http://srv:9780/unifiedlogin/connect/authorize", Endpoints.Authorize.ToString());
        Assert.Equal("http://srv:9780/unifiedlogin/connect/token", Endpoints.Token.ToString());
        Assert.Equal("http://srv:9780/unifiedlogin/api/AuthComplete", Endpoints.AuthComplete.ToString());
        Assert.Equal(
            "http://srv:9780/unifiedlogin/api/ClientScopes?clientId=ab%20c&includeOfflineAccess=True",
            Endpoints.ClientScopes("ab c").AbsoluteUri);
    }

    [Fact]
    public void ActionWaitAddressIsTakenAsIsAndAwaitIsAppended()
    {
        Assert.Equal("http://srv:9780/actionwait/await", Endpoints.ActionWaitAwait.ToString());

        var bare = Endpoints with { ActionWait = new Uri("https://aw.example.com/") };
        Assert.Equal("https://aw.example.com/await", bare.ActionWaitAwait.ToString());
    }

    [Fact]
    public void BrowserLinkContainsAllPkceParametersAndIsEncoded()
    {
        Uri url = OAuthClient.BuildAuthorizeUrl(
            Endpoints, "client-1", ["openid", "a365:workspace:abc"], "state-1", "challenge-1");

        string link = url.AbsoluteUri;
        Assert.StartsWith("http://srv:9780/unifiedlogin/connect/authorize?", link);
        Assert.Contains("client_id=client-1", link);
        Assert.Contains("response_type=code", link);
        Assert.Contains("scope=openid%20a365%3Aworkspace%3Aabc", link);
        Assert.Contains("redirect_uri=http%3A%2F%2Fsrv%3A9780%2Funifiedlogin%2Fapi%2FAuthComplete", link);
        Assert.Contains("code_challenge=challenge-1", link);
        Assert.Contains("code_challenge_method=S256", link);
        Assert.Contains("state=state-1", link);
        Assert.DoesNotContain(' ', link);
    }

    [Fact]
    public void ScopesAreParsedFromArrayAndEmptyResponseMeansUnknownApplication()
    {
        Assert.Equal(["openid", "offline_access"], OAuthClient.ParseScopes("""["openid","offline_access"]"""));
        Assert.Empty(OAuthClient.ParseScopes("[]"));
        Assert.Empty(OAuthClient.ParseScopes("not json"));
    }

    [Theory]
    [InlineData("""{"ActionResult":"Ok","Data":{"code":"C1","error":null}}""", "C1", null)]
    [InlineData("""{"actionResult":0,"data":{"Code":"C2"}}""", "C2", null)]
    [InlineData("""{"Data":{"error":"access_denied","error_description":"denied"}}""", null, "access_denied (denied)")]
    [InlineData("""{"code":"C3"}""", "C3", null)]
    [InlineData("""{"ActionResult":"Timeout","Data":{}}""", null, null)]
    [InlineData("garbage", null, null)]
    public void ActionWaitResponseIsParsedByDataFieldsOrRoot(string json, string? code, string? error)
    {
        ActionWaitResult result = OAuthClient.ParseActionWait(json);

        Assert.Equal(code, result.Code);
        Assert.Equal(error, result.Error);
    }

    [Fact]
    public void TokenExpiryIsTakenFromJwtExp()
    {
        string access = Jwt(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), name: "\u0418\u0432\u0430\u043d");
        string json = $$"""{"access_token":"{{access}}","refresh_token":"R1","id_token":"{{Jwt(DateTimeOffset.UnixEpoch, name: "\u0418\u0432\u0430\u043d")}}","expires_in":999}""";

        TokenSet tokens = OAuthClient.ParseTokenResponse(json, new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), tokens.ExpiresAt);
        Assert.Equal("R1", tokens.RefreshToken);
        Assert.Equal("\u0418\u0432\u0430\u043d", tokens.Account);
    }

    [Fact]
    public void WithoutExpExpiryIsTakenFromExpiresInAndRefreshStaysOld()
    {
        var now = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

        TokenSet tokens = OAuthClient.ParseTokenResponse(
            """{"access_token":"not-a-jwt","expires_in":3600}""", now, previousRefreshToken: "OLD");

        Assert.Equal(now.AddHours(1), tokens.ExpiresAt);
        Assert.Equal("OLD", tokens.RefreshToken);
        Assert.Null(tokens.Account);
    }

    [Fact]
    public void ResponseWithoutAccessTokenIsRejectedWithClearText()
    {
        var error = Assert.Throws<OAuthRejectedException>(
            () => OAuthClient.ParseTokenResponse("""{"error":"x"}""", DateTimeOffset.UtcNow));

        Assert.Contains("access_token", error.Message);
    }

    [Theory]
    [InlineData(100, 60, false)]
    [InlineData(61, 60, false)]
    [InlineData(60, 60, true)]
    [InlineData(-5, 60, true)]
    public void RefreshIsNeededAMinuteBeforeExpiry(int secondsLeft, int skewSeconds, bool expected)
    {
        var now = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        var tokens = new TokenSet("a", "r", null, now.AddSeconds(secondsLeft));

        Assert.Equal(expected, tokens.NeedsRefresh(now, TimeSpan.FromSeconds(skewSeconds)));
    }

    /// <summary>A JWT without a real signature: the client reads only the fields, the signature is checked by the server.</summary>
    internal static string Jwt(DateTimeOffset expires, string? name = null)
    {
        string payload = name is null
            ? $$"""{"exp":{{expires.ToUnixTimeSeconds()}}}"""
            : $$"""{"exp":{{expires.ToUnixTimeSeconds()}},"name":"{{name}}"}""";

        return Pkce.Base64Url(Encoding.UTF8.GetBytes("""{"alg":"none"}""")) + "."
            + Pkce.Base64Url(Encoding.UTF8.GetBytes(payload)) + ".sig";
    }
}
