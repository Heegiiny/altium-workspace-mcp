using System.Text.Json.Serialization;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>The saved session identifier and the data by which its suitability is checked.</summary>
public sealed class PersistedSession
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("baseUrl")]
    public required string BaseUrl { get; init; }

    /// <summary>The account under which the session was obtained; empty for a Windows SSO login.</summary>
    [JsonPropertyName("userName")]
    public string? UserName { get; init; }

    [JsonPropertyName("establishedAt")]
    public DateTimeOffset EstablishedAt { get; init; }
}

/// <summary>
/// A session identifier store that survives restarts.
/// <para>
/// The number of concurrent sessions on the Altium server is limited by the license, and the identifier
/// lives about a month. So a new process first tries to continue the previously
/// issued session and creates a new one only when the old one is really dead.
/// Without this every restart of the MCP server would take one more slot.
/// </para>
/// <para>
/// The identifier is a JWT, that is, full credentials, so it goes to disk
/// encrypted with DPAPI for the current Windows user, and outside Windows — as a file
/// with permissions 600 (<see cref="ProtectedFile{T}"/>). It is not used for token login:
/// the tokens are kept by <see cref="Auth.TokenStore"/> and only with <c>ALTIUM_TOKEN_STORE=file</c>.
/// </para>
/// </summary>
public sealed class SessionStore
{
    // The entropy and the file name are the same as before: already saved sessions keep being read.
    private readonly ProtectedFile<PersistedSession> _file;

    public SessionStore(string stateDirectory) =>
        _file = new ProtectedFile<PersistedSession>(
            System.IO.Path.Combine(stateDirectory, "session.bin"), "AltiumWorkspaceMCP.session.v1");

    public string FilePath => _file.FilePath;

    /// <summary>Reads the saved session. A damaged or foreign file is silently discarded.</summary>
    public PersistedSession? Load() => _file.Load();

    public void Save(PersistedSession session) => _file.Save(session);

    public void Clear() => _file.Clear();
}
