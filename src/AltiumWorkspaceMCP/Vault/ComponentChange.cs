using System.Globalization;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>
/// Roles of component revision links. The role is stored in the HRID field of the link itself,
/// and Altium uses it to tell a symbol from a footprint and a template.
/// </summary>
public static class LinkRole
{
    /// <summary>Schematic symbol.</summary>
    public const string Symbol = "SCHLIB";

    /// <summary>
    /// The primary PCB footprint. Additional ones are stored with the role "PCBLIB 1", "PCBLIB 2" and
    /// so on (<see cref="IsFootprint"/>, <see cref="FootprintRole"/>): the number matches the link's FootprintIndex.
    /// </summary>
    public const string Footprint = "PCBLIB";

    /// <summary>
    /// The whole footprint set of a part: the first is primary. The vault has no such role — it is an instruction
    /// for assembling links (<see cref="LinkAssignment.TargetRevisionGuids"/>), which is expanded
    /// into the links "PCBLIB", "PCBLIB 1", "PCBLIB 2"…
    /// </summary>
    public const string Footprints = "footprints";

    private const string FootprintPrefix = Footprint + " ";

    /// <summary>
    /// A footprint role: "PCBLIB" (primary) or "PCBLIB n", where n is an integer greater than zero
    /// (additional). This is how Altium Designer writes them.
    /// </summary>
    public static bool IsFootprint(string? hrid) => FootprintNumber(hrid) is not null;

    /// <summary>
    /// The footprint number by link role: 0 — "PCBLIB" (primary), n — "PCBLIB n";
    /// <see langword="null"/> — the role is not a footprint one.
    /// </summary>
    public static int? FootprintNumber(string? hrid)
    {
        if (hrid is null)
        {
            return null;
        }

        string role = hrid.Trim();

        if (string.Equals(role, Footprint, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (role.StartsWith(FootprintPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string digits = role[FootprintPrefix.Length..];

            if (digits.Length > 0
                && digits.All(char.IsAsciiDigit)
                && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                && number > 0)
            {
                return number;
            }
        }

        return null;
    }

    /// <summary>The footprint link role with number <paramref name="number"/>: 0 — "PCBLIB", n — "PCBLIB n".</summary>
    public static string FootprintRole(int number) =>
        number <= 0 ? Footprint : FootprintPrefix + number.ToString(CultureInfo.InvariantCulture);

    /// <summary>Component template — it sets the component type.</summary>
    public const string Template = "ComponentTemplate";

    /// <summary>Link to a datasheet.</summary>
    public const string Datasheet = "DATASHEET";

    /// <summary>Simulation model.</summary>
    public const string Simulation = "SIM";

    /// <summary>A readable role name by its code.</summary>
    public static string Describe(string? role) =>
        FootprintNumber(role) is > 0 and var number
            ? $"additional footprint {number}"
            : DescribeKnown(role);

    private static string DescribeKnown(string? role) => role?.Trim().ToUpperInvariant() switch
    {
        Symbol => "symbol",
        Footprint => "footprint",
        "FOOTPRINTS" => "footprints",
        "COMPONENTTEMPLATE" => "component template",
        Datasheet => "datasheet",
        Simulation => "simulation model",
        null or "" => "no role",
        _ => role!,
    };

    /// <summary>The role names accepted on input, for refusal texts.</summary>
    public const string AllowedOnInput = "symbol, footprint, footprints, template, datasheet, simulation";

    /// <summary>
    /// Brings a role name to the form the server understands. A name that is not recognized is returned
    /// trimmed as is: the same method reads the roles of links that already exist in the vault.
    /// </summary>
    public static string Normalize(string role) => role.Trim().ToLowerInvariant() switch
    {
        "symbol" or "schlib" or "sch" => Symbol,
        "footprint" or "pcblib" or "pcb" => Footprint,
        "footprints" or "pcblibs" => Footprints,
        "template" or "componenttemplate" => Template,
        "datasheet" => Datasheet,
        "simulation" or "sim" => Simulation,
        _ => role.Trim(),
    };

    /// <summary>
    /// The role of a link given by the caller: <see cref="Normalize"/> plus a refusal for an unknown role,
    /// otherwise a link with a made-up role would be written to the vault.
    /// </summary>
    public static string Parse(string? role)
    {
        string normalized = Normalize(role ?? string.Empty);

        if (normalized is Symbol or Footprint or Footprints or Template or Datasheet or Simulation
            || IsFootprint(normalized))
        {
            return normalized;
        }

        throw new ArgumentException(
            $"Link role '{role}' not recognized. Allowed: {AllowedOnInput}; "
            + "for an additional footprint — 'PCBLIB 1', 'PCBLIB 2' and so on.", nameof(role));
    }
}

/// <summary>Replacing one component link with another revision.</summary>
/// <param name="Role">
/// Link role: symbol, footprint, template; <see cref="LinkRole.Footprints"/> — the whole footprint set.
/// </param>
/// <param name="TargetRevisionGuid">
/// The revision to reference. Empty — the link is removed. Not used for the <see cref="LinkRole.Footprints"/> role.
/// </param>
/// <param name="TargetRevisionGuids">
/// Only for the <see cref="LinkRole.Footprints"/> role: footprint revisions as a list, the first is primary.
/// An empty list removes all footprints.
/// </param>
/// <param name="Append">
/// Only for the <see cref="LinkRole.Footprint"/> role: do not replace the primary footprint but add
/// <paramref name="TargetRevisionGuid"/> as an additional one under the next number (for a part without footprints — as primary).
/// </param>
public sealed record LinkAssignment(
    string Role,
    string? TargetRevisionGuid,
    IReadOnlyList<string>? TargetRevisionGuids = null,
    bool Append = false);

/// <summary>How a footprint is linked when creating a model for a part (vault_model_files create).</summary>
public enum FootprintMode
{
    /// <summary>The new one becomes primary instead of the old primary.</summary>
    Replace,

    /// <summary>The new one is added as an additional one, with the last number.</summary>
    Add,
}

/// <summary>Parsing of the footprint link mode.</summary>
public static class FootprintModes
{
    /// <summary>"replace" (default, empty) or "add"; anything else — a refusal with a hint.</summary>
    public static FootprintMode Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        null or "" or "replace" => FootprintMode.Replace,
        "add" => FootprintMode.Add,
        _ => throw new ArgumentException(
            $"footprintMode '{text}' not recognized. Allowed: replace (the new footprint becomes primary, default) "
            + "and add (the new one is added to the existing ones).", nameof(text)),
    };
}

/// <summary>Moving links from old model revisions to the new one, whatever role they have.</summary>
/// <param name="FromRevisionGuids">Old model revisions.</param>
/// <param name="ToRevisionGuid">The revision the links are moved to.</param>
/// <param name="Label">How to describe the move in the report, for example "PCC-000-000007 rev. 4".</param>
public sealed record LinkRetarget(IReadOnlySet<string> FromRevisionGuids, string ToRevisionGuid, string Label);

/// <summary>What to change in a component in one operation.</summary>
public sealed record ComponentChange
{
    /// <summary>New parameter values: name → value.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Names of parameters to remove from the revision entirely — not clear the value,
    /// but delete the key itself. A name cannot also be in <see cref="Parameters"/>:
    /// such an edit is contradictory and is rejected when the revision is assembled.
    /// </summary>
    public IReadOnlySet<string> DeleteParameters { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>New description; <see langword="null"/> — keep the old one.</summary>
    public string? Description { get; init; }

    /// <summary>New component name (the Comment column); <see langword="null"/> — keep the old one.</summary>
    public string? Comment { get; init; }

    /// <summary>Relink of a symbol, footprint or template.</summary>
    public IReadOnlyList<LinkAssignment> Links { get; init; } = [];

    /// <summary>Moving links to a new model revision — after releasing an edited file.</summary>
    public IReadOnlyList<LinkRetarget> Retargets { get; init; } = [];

    public bool IsEmpty =>
        Parameters.Count == 0 && DeleteParameters.Count == 0 && Description is null && Comment is null
        && Links.Count == 0 && Retargets.Count == 0;
}
