using AltiumWorkspaceMCP.Auth;

namespace AltiumWorkspaceMCP.Configuration;

/// <summary>How the server treats operations that change the vault.</summary>
public enum WriteMode
{
    /// <summary>Every write is rejected. Only reading and preview are available.</summary>
    ReadOnly,

    /// <summary>
    /// Every edit is applied immediately, ignoring <see cref="VaultOptions.ConfirmThreshold"/>;
    /// the audit log is kept.
    /// </summary>
    Unguarded,

    /// <summary>
    /// Edits up to <see cref="VaultOptions.ConfirmThreshold"/> objects (components, folders
    /// or templates) are applied at once — the vault is protected by revisions, the trash and
    /// a daily backup anyway. Larger edits require a preliminary
    /// preview and confirmation by token. The default mode.
    /// </summary>
    Guarded,
}

/// <summary>Connection settings and write safety rules.</summary>
public sealed class VaultOptions
{
    /// <summary>Workspace root address, for example http://server:9780.</summary>
    public required Uri BaseUrl { get; init; }

    /// <summary>
    /// User name for login by user name and password. Empty — login with the Windows
    /// account of the current user (NTLM SSO), no password is needed then.
    /// </summary>
    public string? UserName { get; init; }

    public string? Password { get; init; }

    /// <summary>Login method (<c>ALTIUM_AUTH</c>): Windows/user name or token via browser.</summary>
    public AuthMethod AuthMethod { get; init; } = AuthMethod.Windows;

    /// <summary>
    /// Application identifier (client_id) for token login — <c>ALTIUM_OAUTH_CLIENT_ID</c>.
    /// It is not hard-coded: the login server issues it and the user chooses it.
    /// </summary>
    public string? OAuthClientId { get; init; }

    /// <summary>Where to keep tokens (<c>ALTIUM_TOKEN_STORE</c>): memory or file.</summary>
    public TokenStoreMode TokenStore { get; init; } = TokenStoreMode.Memory;

    public WriteMode WriteMode { get; init; } = WriteMode.Guarded;

    /// <summary>
    /// How many objects may be changed without confirmation in
    /// <see cref="WriteMode.Guarded"/>.
    /// </summary>
    public int ConfirmThreshold { get; init; } = 4;

    /// <summary>
    /// If set, writes are allowed only inside the listed folder paths
    /// (prefix comparison, case-insensitive). Empty — no restriction.
    /// </summary>
    public IReadOnlyList<string> WritableFolders { get; init; } = [];

    /// <summary>
    /// The largest number of components changed by one tool call.
    /// </summary>
    /// <remarks>
    /// The server creates and releases about four revisions per second, and that is its
    /// limit, not a consequence of the number of requests. Editing two thousand components takes
    /// about ten minutes and does not fit the client's call timeout,
    /// so the excess is moved to the next call instead of being cut off by time.
    /// </remarks>
    public int MaxWriteBatch { get; init; } = 500;

    /// <summary>
    /// The largest tool response length in characters (<c>ALTIUM_MAX_RESPONSE_CHARS</c>).
    /// </summary>
    /// <remarks>
    /// The Claude client accepts a result of roughly up to 25 thousand tokens and drops a larger one
    /// entirely. Paged listings fit this limit by themselves, and the general filter replaces
    /// a response that got through with a refusal and advice on how to narrow the selection.
    /// </remarks>
    public int MaxResponseChars { get; init; } = 20000;

    /// <summary>Directory for the session file and the audit log.</summary>
    public required string StateDirectory { get; init; }

    /// <summary>
    /// Directory for exchanging model files: symbol and footprint libraries are exported
    /// here so that altium-designer-mcp can open them.
    /// </summary>
    public required string ExchangeDirectory { get; init; }

    /// <summary>
    /// The same exchange directory as the agent's paths show it, for example /home/agent/altium-libraries,
    /// if the agent sees the file system differently from the server. Empty — the paths are the same.
    /// </summary>
    public string? ExchangeAgentDirectory { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    public bool UsesTokenAuthentication => AuthMethod == AuthMethod.Token;

    public bool UsesWindowsAuthentication => !UsesTokenAuthentication && string.IsNullOrWhiteSpace(UserName);

    public static VaultOptions FromEnvironment()
    {
        string baseUrl = Environment.GetEnvironmentVariable("ALTIUM_BASE_URL")
            ?? throw new InvalidOperationException(
                "ALTIUM_BASE_URL is not set — the address of the Altium workspace, "
                + "for example http://vault.example.local:9780");

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? parsed))
        {
            throw new InvalidOperationException($"ALTIUM_BASE_URL is not a valid address: {baseUrl}");
        }

        string stateDirectory = Environment.GetEnvironmentVariable("ALTIUM_STATE_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AltiumVaultMcp");

        string? userName = Environment.GetEnvironmentVariable("ALTIUM_USERNAME");

        return new VaultOptions
        {
            BaseUrl = parsed,
            UserName = userName,
            Password = Environment.GetEnvironmentVariable("ALTIUM_PASSWORD"),
            AuthMethod = AuthSettings.ResolveMethod(
                Environment.GetEnvironmentVariable("ALTIUM_AUTH"), userName, OperatingSystem.IsWindows()),
            OAuthClientId = Environment.GetEnvironmentVariable("ALTIUM_OAUTH_CLIENT_ID") is { Length: > 0 } clientId
                ? clientId.Trim()
                : null,
            TokenStore = AuthSettings.ResolveTokenStore(Environment.GetEnvironmentVariable("ALTIUM_TOKEN_STORE")),
            WriteMode = ParseWriteMode(Environment.GetEnvironmentVariable("ALTIUM_WRITE_MODE")),
            ConfirmThreshold = ParseThreshold(Environment.GetEnvironmentVariable("ALTIUM_CONFIRM_THRESHOLD")),
            WritableFolders = ParseFolders(Environment.GetEnvironmentVariable("ALTIUM_WRITABLE_FOLDERS")),
            MaxWriteBatch = ParsePositive(
                Environment.GetEnvironmentVariable("ALTIUM_MAX_WRITE_BATCH"), 500, "ALTIUM_MAX_WRITE_BATCH"),
            MaxResponseChars = ParsePositive(
                Environment.GetEnvironmentVariable("ALTIUM_MAX_RESPONSE_CHARS"), 20000, "ALTIUM_MAX_RESPONSE_CHARS"),
            StateDirectory = stateDirectory,
            ExchangeDirectory = Environment.GetEnvironmentVariable("ALTIUM_EXCHANGE_DIR") is { Length: > 0 } exchange
                ? exchange
                : Path.Combine(stateDirectory, "exchange"),
            ExchangeAgentDirectory = Environment.GetEnvironmentVariable("ALTIUM_EXCHANGE_AGENT_DIR") is { Length: > 0 } agent
                ? agent
                : null,
        };
    }

    private static WriteMode ParseWriteMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "guarded" => WriteMode.Guarded,
        "readonly" or "read-only" => WriteMode.ReadOnly,
        "unguarded" => WriteMode.Unguarded,
        _ => throw new InvalidOperationException(
            $"ALTIUM_WRITE_MODE has an invalid value '{value}'. "
            + "Allowed: readonly, guarded, unguarded."),
    };

    private static int ParseThreshold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 4;
        }

        if (!int.TryParse(value, out int parsed) || parsed < 0)
        {
            throw new InvalidOperationException(
                $"ALTIUM_CONFIRM_THRESHOLD must be a non-negative number, got '{value}'.");
        }

        return parsed;
    }

    private static int ParsePositive(string? value, int fallback, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!int.TryParse(value, out int parsed) || parsed <= 0)
        {
            throw new InvalidOperationException($"{name} must be a positive number, got '{value}'.");
        }

        return parsed;
    }

    private static IReadOnlyList<string> ParseFolders(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
