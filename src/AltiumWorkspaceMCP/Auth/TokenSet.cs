using System.Text;
using System.Text.Json;

namespace AltiumWorkspaceMCP.Auth;

/// <summary>The tokens issued by the login server. The access token (JWT) serves as the workspace session identifier.</summary>
/// <param name="AccessToken">The access token — also the session identifier.</param>
/// <param name="RefreshToken">The refresh token; present only if offline_access was requested.</param>
/// <param name="IdToken">The identity token: the user name for display is taken from it.</param>
/// <param name="ExpiresAt">When the access token stops being valid.</param>
public sealed record TokenSet(string AccessToken, string? RefreshToken, string? IdToken, DateTimeOffset ExpiresAt)
{
    /// <summary>Whether it is time to refresh: less than <paramref name="skew"/> is left until expiry.</summary>
    public bool NeedsRefresh(DateTimeOffset now, TimeSpan skew) => now + skew >= ExpiresAt;

    /// <summary>The account name for display (without secrets); empty if the tokens do not contain it.</summary>
    public string? Account => JwtClaims.FirstString(IdToken, "name", "preferred_username", "username", "email")
        ?? JwtClaims.FirstString(AccessToken, "name", "preferred_username", "username", "email", "sub");
}

/// <summary>Reading JWT fields without checking the signature: the expiry and the name are needed only for display and for planning the refresh.</summary>
internal static class JwtClaims
{
    /// <summary>The expiry from the <c>exp</c> field; <c>null</c> if it is not a JWT or has no such field.</summary>
    public static DateTimeOffset? Expiry(string? jwt)
    {
        using JsonDocument? payload = Payload(jwt);
        if (payload is not null
            && payload.RootElement.TryGetProperty("exp", out JsonElement exp)
            && exp.ValueKind == JsonValueKind.Number
            && exp.TryGetInt64(out long seconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        return null;
    }

    public static string? FirstString(string? jwt, params string[] names)
    {
        using JsonDocument? payload = Payload(jwt);
        if (payload is null)
        {
            return null;
        }

        foreach (string name in names)
        {
            if (payload.RootElement.TryGetProperty(name, out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static JsonDocument? Payload(string? jwt)
    {
        string[] parts = (jwt ?? string.Empty).Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            string base64 = parts[1].Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }
    }
}
