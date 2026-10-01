using System.Text;
using System.Text.RegularExpressions;

namespace AltiumWorkspaceMCP.Vault.Rest;

/// <summary>
/// Field names of the workspace search service (<c>/search/v1.0/searchasync</c>).
/// </summary>
/// <remarks>
/// A field is a parameter name with escaping and a group suffix. Escaping: every character
/// other than Latin letters and digits is replaced by <c>_XX</c> (a hexadecimal code, upper case):
/// space → <c>_20</c>, <c>/</c> → <c>_2F</c>, <c>_</c> → <c>_5F</c>, <c>-</c> → <c>_2D</c>,
/// <c>(</c> → <c>_28</c>, <c>#</c> → <c>_23</c>. The suffix sets the group: parameters and system fields
/// of the panel — <see cref="ParametersGroup"/>, the revision core (<c>Id</c>, <c>HRID</c>, <c>Comment</c>,
/// <c>Description</c>, <c>FolderGUID</c>, <c>LifeCycleStateGUID</c>) — <see cref="RevisionCoreGroup"/>.
/// The numeric value of a parameter is in a separate field whose name is built from the parameter name and
/// the GUID of its type (<see cref="NumericParameter"/>). Checked against Altium Designer traffic.
/// </remarks>
public static partial class SearchFieldNames
{
    /// <summary>Suffix of the group "parameters and system fields of the panel".</summary>
    public const string ParametersGroup = "DD420E8DDD8B445E911A0601BB2B6D53";

    /// <summary>Suffix of the group "revision core".</summary>
    public const string RevisionCoreGroup = "C623975962814A5FAAD7FA1CD85DA0DB";

    /// <summary>Escapes a name: everything except Latin letters and digits becomes <c>_XX</c>.</summary>
    public static string Escape(string name)
    {
        var result = new StringBuilder(name.Length + 8);

        foreach (char symbol in name)
        {
            if (symbol is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                result.Append(symbol);
            }
            else
            {
                // Characters outside Latin-1 did not occur in the captures: for them the full code in four characters is taken.
                result.Append('_').Append(symbol < 0x100 ? ((int)symbol).ToString("X2") : ((int)symbol).ToString("X4"));
            }
        }

        return result.ToString();
    }

    /// <summary>A parameter field or a system field of the panel: <c>Case_2FPackageDD42…</c>, <c>ContentTypeDD42…</c>.</summary>
    public static string Parameter(string name) => Escape(name) + ParametersGroup;

    /// <summary>A revision core field: <c>IdC623…</c>, <c>HRIDC623…</c>, <c>FolderFullPathC623…</c>.</summary>
    public static string Core(string name) => Escape(name) + RevisionCoreGroup;

    /// <summary>
    /// The numeric value of a parameter in base units (for example 1000 for "1k"):
    /// <c>Value_5FB90F0DAE_2DB695_…</c> — the parameter name, "_" and the parameter type GUID.
    /// </summary>
    public static string NumericParameter(string name, string parameterTypeGuid) =>
        Escape($"{name}_{parameterTypeGuid}") + ParametersGroup;

    /// <summary>Component type in a search field: the full path with a trailing backslash.</summary>
    public static string ComponentTypeValue(string path) => path.TrimEnd('\\') + "\\";

    /// <summary>How many footprints the index stores per part: the fields <c>FootprintName1…14</c>.</summary>
    public const int MaxFootprintNames = 14;

    /// <summary>How many footprint revisions the index stores per part: the fields <c>FootprintRevisionID1…8</c>.</summary>
    public const int MaxFootprintRevisions = 8;

    /// <summary>Name of footprint number <paramref name="number"/> (from 1): <c>FootprintName1DD42…</c>.</summary>
    public static string FootprintName(int number) => Parameter($"FootprintName{number}");

    /// <summary>
    /// Revision of footprint number <paramref name="number"/> (from 1), a record like <c>PCC-0009-3</c>
    /// (the item HRID and the revision number); the numbering is the same as for names.
    /// </summary>
    public static string FootprintRevision(int number) => Parameter($"FootprintRevisionID{number}");

    /// <summary>Suffix of the text "mirror" of a numeric parameter: <c>Value_T@x^</c>.</summary>
    public const string TextMirrorSuffix = "_T@x^";

    [GeneratedRegex("_([0-9A-Fa-f]{2})")]
    private static partial Regex EscapePattern();

    [GeneratedRegex("^(?<name>.+)_(?<guid>[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})$")]
    private static partial Regex NumericPattern();

    /// <summary>The inverse of <see cref="Escape"/>: <c>_XX</c> → the character with this code.</summary>
    public static string Unescape(string escaped) =>
        EscapePattern().Replace(escaped, match => ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString());

    /// <summary>
    /// Parses a field or facet name: the group, the parameter name and the field kind. <c>null</c> — the suffix
    /// does not belong to any of the known groups.
    /// </summary>
    public static ParsedFieldName? Parse(string field)
    {
        FieldGroup group;
        string escaped;

        if (field.EndsWith(ParametersGroup, StringComparison.Ordinal))
        {
            group = FieldGroup.Parameters;
            escaped = field[..^ParametersGroup.Length];
        }
        else if (field.EndsWith(RevisionCoreGroup, StringComparison.Ordinal))
        {
            group = FieldGroup.RevisionCore;
            escaped = field[..^RevisionCoreGroup.Length];
        }
        else
        {
            return null;
        }

        string name = Unescape(escaped);

        if (name.EndsWith(TextMirrorSuffix, StringComparison.Ordinal) && name.Length > TextMirrorSuffix.Length)
        {
            return new ParsedFieldName(group, name[..^TextMirrorSuffix.Length], FieldKind.TextMirror, null, field);
        }

        Match numeric = NumericPattern().Match(name);
        return numeric.Success
            ? new ParsedFieldName(group, numeric.Groups["name"].Value, FieldKind.Numeric, numeric.Groups["guid"].Value.ToUpperInvariant(), field)
            : new ParsedFieldName(group, name, FieldKind.Plain, null, field);
    }
}

/// <summary>Search service field group (the name suffix).</summary>
public enum FieldGroup
{
    /// <summary>Parameters and system fields of the panel (<c>DD42…</c>).</summary>
    Parameters,

    /// <summary>Revision core (<c>C623…</c>).</summary>
    RevisionCore,
}

/// <summary>Kind of a parameter field.</summary>
public enum FieldKind
{
    /// <summary>The value as text (an ordinary parameter).</summary>
    Plain,

    /// <summary>The displayed value of a numeric parameter (<c>Value_T@x^</c>).</summary>
    TextMirror,

    /// <summary>Number in base units; the name has the type GUID.</summary>
    Numeric,
}

/// <summary>A parsed field name: <paramref name="Parameter"/> — the parameter name without service parts.</summary>
public sealed record ParsedFieldName(FieldGroup Group, string Parameter, FieldKind Kind, string? TypeGuid, string Field);
