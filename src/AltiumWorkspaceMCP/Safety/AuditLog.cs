using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AltiumWorkspaceMCP.Safety;

/// <summary>One entry of the vault change log.</summary>
public sealed record AuditEntry
{
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    [JsonPropertyName("operation")]
    public required string Operation { get; init; }

    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    [JsonPropertyName("affected")]
    public int Affected { get; init; }

    /// <summary>Plan number if the change was charged to the active plan.</summary>
    [JsonPropertyName("plan")]
    public int? Plan { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    /// <summary>State of the objects before the change — the basis for a manual rollback.</summary>
    [JsonPropertyName("before")]
    public object? Before { get; init; }

    [JsonPropertyName("after")]
    public object? After { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>
/// Append-only change log in JSON Lines format. It keeps the previous values,
/// so what was there before an edit can be restored from it.
/// </summary>
public sealed class AuditLog
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AuditLog(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        _path = Path.Combine(stateDirectory, "audit.jsonl");
    }

    public string FilePath => _path;

    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        // A dry run changes nothing and does not belong in the change log.
        if (Vault.DryRun.IsActive)
        {
            return;
        }

        string line = JsonSerializer.Serialize(entry, SerializerOptions);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(_path, line + Environment.NewLine, Encoding.UTF8, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Returns the latest log entries, newest first.</summary>
    public async Task<IReadOnlyList<string>> ReadRecentAsync(
        int count,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            string[] lines = await File.ReadAllLinesAsync(_path, Encoding.UTF8, cancellationToken);
            return lines.Reverse().Take(count).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }
}
