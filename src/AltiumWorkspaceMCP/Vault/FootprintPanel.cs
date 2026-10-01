using AltiumWorkspaceMCP.Soap.Vault;
using AltiumWorkspaceMCP.Vault.Rest;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>Footprints of a part by an index document: names and revisions in the order of field numbers.</summary>
public sealed record FootprintInfo(IReadOnlyList<string> Names, IReadOnlyList<string> Revisions)
{
    public static FootprintInfo Empty { get; } = new([], []);
}

/// <summary>
/// Footprints and folders in the search index: the fields <c>FootprintName1…14</c>, <c>FootprintRevisionID1…8</c>,
/// <c>FolderFullPath</c>. Pure logic without server calls.
/// </summary>
public static class FootprintPanel
{
    /// <summary>Name of the virtual <c>list</c> column: the footprint (for several — comma-separated with a count).</summary>
    public const string ColumnName = "footprint";

    /// <summary>Name of the virtual <c>list</c> column: the footprint revision like <c>PCC-0009-3</c>.</summary>
    public const string RevisionColumnName = "footprintRevision";

    /// <summary>A column name in <c>columns</c> is a virtual column, not a part parameter.</summary>
    public static bool IsColumn(string name) =>
        name.Trim().Equals(ColumnName, StringComparison.OrdinalIgnoreCase)
        || name.Trim().Equals(RevisionColumnName, StringComparison.OrdinalIgnoreCase);

    public static bool IsRevisionColumn(string name) =>
        name.Trim().Equals(RevisionColumnName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Index fields to return in order to read the footprint names (and revisions).</summary>
    public static IReadOnlyList<string> ReturnFields(bool names, bool revisions)
    {
        var fields = new List<string>();

        if (names)
        {
            fields.AddRange(Enumerable.Range(1, SearchFieldNames.MaxFootprintNames).Select(SearchFieldNames.FootprintName));
        }

        if (revisions)
        {
            fields.AddRange(Enumerable.Range(1, SearchFieldNames.MaxFootprintRevisions).Select(SearchFieldNames.FootprintRevision));
        }

        return fields;
    }

    /// <summary>A part with the footprint <paramref name="name"/>: a strict match with any of the numbers 1…14.</summary>
    public static BooleanCondition Condition(string name)
    {
        BooleanCondition group = BooleanCondition.Empty;

        foreach (int number in Enumerable.Range(1, SearchFieldNames.MaxFootprintNames))
        {
            group = group.With(new StrictCondition(SearchFieldNames.FootprintName(number), name.Trim()), SearchOccur.Should);
        }

        return group;
    }

    /// <summary>Reads the footprint names and revisions of a document; empty values are skipped.</summary>
    public static FootprintInfo Read(SearchDocument document)
    {
        var names = new List<string>();
        var revisions = new List<string>();

        foreach (int number in Enumerable.Range(1, SearchFieldNames.MaxFootprintNames))
        {
            if (document.Get(SearchFieldNames.FootprintName(number)) is { Length: > 0 } name)
            {
                names.Add(name);
            }
        }

        foreach (int number in Enumerable.Range(1, SearchFieldNames.MaxFootprintRevisions))
        {
            if (document.Get(SearchFieldNames.FootprintRevision(number)) is { Length: > 0 } revision)
            {
                revisions.Add(revision);
            }
        }

        return new FootprintInfo(names, revisions);
    }

    /// <summary>One name as is; several — comma-separated with a count at the end: <c>Juper 200, Juper 300 [2]</c>.</summary>
    public static string? FormatNames(FootprintInfo info) => Format(info.Names);

    /// <summary>Revisions in the same form as names: one as is, several — comma-separated with a count.</summary>
    public static string? FormatRevisions(FootprintInfo info) => Format(info.Revisions);

    private static string? Format(IReadOnlyList<string> values) => values.Count switch
    {
        0 => null,
        1 => values[0],
        _ => $"{string.Join(", ", values)} [{values.Count}]",
    };

    /// <summary>A facet name is one of <c>FootprintName1…14</c> (a parameter group).</summary>
    public static bool IsNameFacet(string facetName) =>
        SearchFieldNames.Parse(facetName) is { Group: FieldGroup.Parameters, Kind: FieldKind.Plain } parsed
        && parsed.Parameter.StartsWith("FootprintName", StringComparison.Ordinal)
        && int.TryParse(parsed.Parameter["FootprintName".Length..], out _);

    /// <summary>
    /// Selection by folder: the folder itself and all nested ones. The value of the <c>FolderFullPath</c> field is the path with a trailing
    /// backslash (<c>Components\Connectors\Headers\</c>), so the pattern <c>path\*</c> does not capture
    /// neighbors with a common name beginning. Checked on the server: the pattern is case-insensitive, while a strict
    /// match is case-sensitive and requires the trailing backslash.
    /// </summary>
    public static SearchCondition FolderCondition(string folderPath) =>
        new WildcardCondition(SearchFieldNames.Core("FolderFullPath"), folderPath.Trim().TrimEnd('\\') + "\\*");

    /// <summary>A folder path from the index without a trailing backslash — as in <c>vault_table</c>.</summary>
    public static string? TrimFolder(string? path) => path?.TrimEnd('\\');

    /// <summary>
    /// Lifecycle states that the Components panel does not show: they have no "applicable"
    /// flag (<c>IsApplicable=false</c>: Obsolete, Abandoned, Deleted). The count and composition matched the
    /// Altium Designer traffic capture (13 states, including both GUIDs from the capture).
    /// </summary>
    public static IReadOnlyList<ALU_LifeCycleState> HiddenStates(IEnumerable<ALU_LifeCycleState> states) =>
        states.Where(state => !state.IsApplicable && !string.IsNullOrEmpty(state.GUID)).ToList();
}
