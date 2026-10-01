using System.Text.Encodings.Web;
using System.Text.Json;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>
/// Moving parts to the active template revision: pure logic without server calls.
/// A part's link to a template points to a specific template revision, and a template edit releases a new one —
/// the parts stay on the old one until they are moved.
/// </summary>
public static class TemplateRelinkPlan
{
    /// <summary>How many lagging parts are named individually in the response.</summary>
    public const int SampleSize = 10;

    private static readonly JsonSerializerOptions CallJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The relink parameter value: <see langword="true"/> — move (all), <see langword="false"/> — do not (none, the default).</summary>
    public static bool ParseRelink(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "" or "none" => false,
        "all" => true,
        _ => throw new ArgumentException($"relink accepts none or all, got '{value}'.", nameof(value)),
    };

    /// <summary>
    /// Which of the lagging parts to move: empty <paramref name="requested"/> — all; otherwise those named
    /// by identifier or GUID. Those named but not lagging are returned separately.
    /// </summary>
    public static (IReadOnlyList<ComponentRecord> Selected, IReadOnlyList<string> Absent) Select(
        IReadOnlyList<ComponentRecord> users,
        IReadOnlyCollection<string> requested)
    {
        var wanted = requested
            .Where(identifier => !string.IsNullOrWhiteSpace(identifier))
            .Select(identifier => identifier.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (wanted.Count == 0)
        {
            return (users, []);
        }

        var selected = users
            .Where(user => wanted.Contains(user.Hrid) || wanted.Contains(user.ItemGuid))
            .ToList();

        var absent = wanted
            .Where(identifier => !selected.Any(user =>
                string.Equals(user.Hrid, identifier, StringComparison.OrdinalIgnoreCase)
                || string.Equals(user.ItemGuid, identifier, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return (selected, absent);
    }

    /// <summary>How many parts still wait for the move after the call: with <paramref name="requested"/> set — only among the named ones.</summary>
    public static int Remaining(
        IReadOnlyCollection<string> stillLagging,
        IReadOnlyCollection<string> requested)
    {
        var wanted = requested
            .Where(identifier => !string.IsNullOrWhiteSpace(identifier))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return wanted.Count == 0
            ? stillLagging.Count
            : stillLagging.Count(hrid => wanted.Contains(hrid));
    }

    /// <summary>
    /// The ready call string by which the lagging parts are moved to the active template revision.
    /// </summary>
    public static string RelinkCall(string templateHrid) =>
        "vault_template " + JsonSerializer.Serialize(
            new Dictionary<string, string> { ["action"] = "relink", ["template"] = templateHrid }, CallJson);

    /// <summary>Description of the lagging parts for the response: the count, the first names and the ready call.</summary>
    public static object DescribeLaggards(string templateHrid, IReadOnlyList<ComponentRecord> laggards) => new
    {
        count = laggards.Count,
        components = laggards.Take(SampleSize).Select(record => record.Hrid).ToList(),
        omitted = laggards.Count > SampleSize ? laggards.Count - SampleSize : (int?)null,
        relinkCall = laggards.Count > 0 ? RelinkCall(templateHrid) : null,
        hint = laggards.Count > 0
            ? "A part's link points to a specific template revision: these parts stayed on the old ones. To move them — "
                + "relinkCall (or relink=all in the same call). If the parts are edited by a migration anyway, it is more efficient "
                + "to add role:template to the same vault_set_links as the footprint: one part revision instead of two. "
                + "The move does not change the part type — it is a tag (vault_component_types assign)."
            : null,
    };
}
