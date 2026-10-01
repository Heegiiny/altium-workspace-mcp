using System.Text.Json.Serialization;
using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Auth;

/// <summary>The tokens in a file and the data by which it is checked that they were issued for this server and application.</summary>
public sealed class PersistedTokens
{
    [JsonPropertyName("baseUrl")]
    public required string BaseUrl { get; init; }

    [JsonPropertyName("clientId")]
    public required string ClientId { get; init; }

    [JsonPropertyName("accessToken")]
    public required string AccessToken { get; init; }

    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("idToken")]
    public string? IdToken { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// The <c>tokens.bin</c> token file in the state directory. Used only with
/// <c>ALTIUM_TOKEN_STORE=file</c>: by default the tokens live in process memory and do not go to disk.
/// Windows — DPAPI for the current user, otherwise permissions 600 (<see cref="ProtectedFile{T}"/>).
/// </summary>
public sealed class TokenStore
{
    private readonly ProtectedFile<PersistedTokens> _file;

    public TokenStore(string stateDirectory) =>
        _file = new ProtectedFile<PersistedTokens>(
            Path.Combine(stateDirectory, "tokens.bin"), "AltiumWorkspaceMCP.tokens.v1");

    public string FilePath => _file.FilePath;

    /// <summary>The tokens issued exactly to this server and application; foreign or damaged ones — <c>null</c>.</summary>
    public TokenSet? Load(Uri baseUrl, string clientId)
    {
        PersistedTokens? saved = _file.Load();

        if (saved is null
            || !string.Equals(saved.ClientId, clientId, StringComparison.Ordinal)
            || !Uri.TryCreate(saved.BaseUrl, UriKind.Absolute, out Uri? storedUrl)
            || storedUrl != baseUrl)
        {
            return null;
        }

        return new TokenSet(saved.AccessToken, saved.RefreshToken, saved.IdToken, saved.ExpiresAt);
    }

    public void Save(Uri baseUrl, string clientId, TokenSet tokens) =>
        _file.Save(new PersistedTokens
        {
            BaseUrl = baseUrl.ToString(),
            ClientId = clientId,
            AccessToken = tokens.AccessToken,
            RefreshToken = tokens.RefreshToken,
            IdToken = tokens.IdToken,
            ExpiresAt = tokens.ExpiresAt,
        });

    public void Clear() => _file.Clear();
}
