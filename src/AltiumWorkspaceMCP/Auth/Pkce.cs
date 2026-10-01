using System.Security.Cryptography;
using System.Text;

namespace AltiumWorkspaceMCP.Auth;

/// <summary>PKCE (RFC 7636, S256 method) — as in the Altium Designer client: a verifier string of 54 random bytes.</summary>
internal static class Pkce
{
    /// <summary>How many random bytes in the verifier string (Designer has 54, that is 72 Base64Url characters).</summary>
    internal const int VerifierBytes = 54;

    public static string CreateVerifier() => Base64Url(RandomNumberGenerator.GetBytes(VerifierBytes));

    /// <summary><c>BASE64URL(SHA256(verifier))</c> without padding characters.</summary>
    public static string CreateChallenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>The <c>state</c> value: the client also names the request in the ActionWait service with it.</summary>
    public static string CreateState() => Guid.NewGuid().ToString();

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
