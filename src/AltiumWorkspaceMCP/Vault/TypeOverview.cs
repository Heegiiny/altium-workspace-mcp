using System.Globalization;
using AltiumWorkspaceMCP.Vault.Rest;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>A parameter in the summary: how many parts have it set and the most frequent values.</summary>
public sealed record OverviewParameter(string Name, int Filled, IReadOnlyList<string> Top);

/// <summary>How many parts of the selection lack something (MPN, LCSC Part#, footprint).</summary>
public sealed record OverviewGaps(long WithoutMpn, long WithoutLcsc, long WithoutFootprint);

/// <summary>A top tree type: the part count and how many of them have no MPN.</summary>
public sealed record OverviewTypeLine(string Type, long Total, long WithoutMpn);

/// <summary>What the selection documents gave: folders, gaps and top types.</summary>
public sealed record DocumentTally(
    long Documents,
    long WithoutType,
    IReadOnlyDictionary<string, long> Folders,
    OverviewGaps Gaps,
    IReadOnlyList<OverviewTypeLine> TopTypes);

/// <summary>Result of <c>overview</c>.</summary>
/// <param name="Parameters">Type parameters by descending fill rate; empty without a type.</param>
/// <param name="Truncated">There are more documents in the selection than were read: the shares by folders and gaps are approximate.</param>
public sealed record OverviewResult(
    ScopeInfo Scope,
    long Total,
    DocumentTally Tally,
    IReadOnlyList<OverviewParameter> Parameters,
    bool Truncated,
    long ElapsedMs);

/// <summary>Pure summary logic: shares, a top with folding, parsing of documents.</summary>
public static class TypeOverview
{
    /// <summary>Parameters with a fill rate below this share are folded into the "rare" line.</summary>
    public const double RareShare = 0.02;

    /// <summary>How many folders the summary shows.</summary>
    public const int TopFolders = 10;

    /// <summary>How many parameter values the summary shows.</summary>
    public const int TopValues = 5;

    public const string MpnName = "Manufacturer Part Number";
    public const string LcscName = "LCSC Part#";

    /// <summary>The share of <paramref name="part"/> in <paramref name="total"/>, percent with one decimal; 0 for an empty selection.</summary>
    public static double Percent(long part, long total) => total <= 0 ? 0 : Math.Round(100.0 * part / total, 1, MidpointRounding.AwayFromZero);

    /// <summary>«1995 (98.5%)».</summary>
    public static string Share(long part, long total) => $"{part} ({Percent(part, total).ToString("0.0", CultureInfo.InvariantCulture)}%)";

    /// <summary>
    /// Top <paramref name="limit"/> by descending count; the rest — as the line "[+N]" (how many more values).
    /// Lines — "value — count".
    /// </summary>
    public static IReadOnlyList<string> TopWithRest(IEnumerable<KeyValuePair<string, long>> counts, int limit)
    {
        var ordered = counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var lines = ordered.Take(limit).Select(pair => $"{pair.Key} — {pair.Value}").ToList();

        if (ordered.Count > limit)
        {
            lines.Add($"[+{ordered.Count - limit}]");
        }

        return lines;
    }

    /// <summary>
    /// Splits parameters into shown (filled in at least <paramref name="rareShare"/> of the selection) and rare ones.
    /// The shown ones go by descending fill rate.
    /// </summary>
    public static (IReadOnlyList<OverviewParameter> Shown, int Rare) FoldRare(
        IEnumerable<OverviewParameter> parameters,
        long total,
        double rareShare = RareShare)
    {
        var shown = new List<OverviewParameter>();
        int rare = 0;

        foreach (OverviewParameter parameter in parameters.OrderByDescending(item => item.Filled).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (total > 0 && parameter.Filled < rareShare * total)
            {
                rare++;
            }
            else
            {
                shown.Add(parameter);
            }
        }

        return (shown, rare);
    }

    /// <summary>
    /// How many parts of the selection have a non-empty field value by facet: the index counts empty values in the facet too
    /// (for example, <c>FootprintDescription1</c> with an empty string on most parts), their count is subtracted.
    /// </summary>
    public static int FilledCount(SearchFacet? facet)
    {
        if (facet is null)
        {
            return 0;
        }

        int empty = facet.Counters.Where(counter => string.IsNullOrWhiteSpace(counter.Value)).Sum(counter => counter.Count);
        return Math.Max(facet.TotalHitCount - empty, 0);
    }

    /// <summary>
    /// Summary parameters from facets: the fill rate without empty values and the top 5 values. One parameter
    /// can correspond to several fields (numeric, the text "mirror" <c>Value_T@x^</c> and the same-named
    /// text one, if some parts have the value written as text): the fill rate — by the most filled one,
    /// the values — from the most filled text one (for a numeric — the displayed one).
    /// </summary>
    public static IReadOnlyList<OverviewParameter> FromFacets(IEnumerable<SearchFacet> facets)
    {
        var byName = new Dictionary<string, List<(SearchFacet Facet, bool Numeric)>>(StringComparer.OrdinalIgnoreCase);

        foreach (SearchFacet facet in facets)
        {
            ParsedFieldName? parsed = SearchFieldNames.Parse(facet.Name);

            if (parsed is not { Group: FieldGroup.Parameters } || PanelParameters.IsSystemName(parsed.Parameter))
            {
                continue;
            }

            if (!byName.TryGetValue(parsed.Parameter, out var list))
            {
                byName[parsed.Parameter] = list = [];
            }

            list.Add((facet, parsed.Kind == FieldKind.Numeric));
        }

        var result = new List<OverviewParameter>();

        foreach ((string name, var list) in byName)
        {
            int filled = list.Max(item => FilledCount(item.Facet));

            // Values are text fields; for a purely numeric parameter — its numeric field.
            SearchFacet? source = list.Where(item => !item.Numeric).OrderByDescending(item => FilledCount(item.Facet)).Select(item => item.Facet).FirstOrDefault()
                ?? list.OrderByDescending(item => FilledCount(item.Facet)).Select(item => item.Facet).FirstOrDefault();

            var top = (source?.Counters ?? [])
                .Where(counter => !string.IsNullOrWhiteSpace(counter.Value))
                .OrderByDescending(counter => counter.Count)
                .ThenBy(counter => counter.Value, StringComparer.Ordinal)
                .Take(TopValues)
                .Select(counter => $"{counter.Value} ({counter.Count})")
                .ToList();

            if (filled > 0)
            {
                result.Add(new OverviewParameter(name, filled, top));
            }
        }

        return result;
    }

    /// <summary>Fields needed from documents for the summary.</summary>
    public static IReadOnlyList<string> ReturnFields { get; } =
    [
        SearchFieldNames.Parameter("ComponentType"),
        SearchFieldNames.Core("FolderFullPath"),
        SearchFieldNames.Parameter(MpnName),
        SearchFieldNames.Parameter(LcscName),
        SearchFieldNames.FootprintName(1),
    ];

    /// <summary>The top level of a type path: <c>Passive\Resistors\Chip\</c> → <c>Passive</c>; empty for a part without a type.</summary>
    public static string TopLevelType(string? typePath)
    {
        string trimmed = (typePath ?? string.Empty).Trim().Trim('\\');
        int slash = trimmed.IndexOf('\\');
        return slash < 0 ? trimmed : trimmed[..slash];
    }

    /// <summary>A folder without a trailing backslash, as in vault_table; empty if there is no field.</summary>
    public static string FolderOf(string? path) => (path ?? string.Empty).Trim().TrimEnd('\\');

    /// <summary>Folds the selection documents: folders, gaps and top types.</summary>
    public static DocumentTally Tally(IEnumerable<SearchDocument> documents)
    {
        string type = SearchFieldNames.Parameter("ComponentType");
        string folder = SearchFieldNames.Core("FolderFullPath");
        string mpn = SearchFieldNames.Parameter(MpnName);
        string lcsc = SearchFieldNames.Parameter(LcscName);
        string footprint = SearchFieldNames.FootprintName(1);

        long count = 0, withoutType = 0, noMpn = 0, noLcsc = 0, noFootprint = 0;
        var folders = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var typeTotal = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var typeNoMpn = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (SearchDocument document in documents)
        {
            count++;
            bool missingMpn = string.IsNullOrWhiteSpace(document.Get(mpn));

            if (missingMpn)
            {
                noMpn++;
            }

            if (string.IsNullOrWhiteSpace(document.Get(lcsc)))
            {
                noLcsc++;
            }

            if (string.IsNullOrWhiteSpace(document.Get(footprint)))
            {
                noFootprint++;
            }

            string folderPath = FolderOf(document.Get(folder));
            string folderKey = folderPath.Length == 0 ? "(no folder)" : folderPath;
            folders[folderKey] = folders.GetValueOrDefault(folderKey) + 1;

            string top = TopLevelType(document.Get(type));

            if (top.Length == 0)
            {
                withoutType++;
                continue;
            }

            typeTotal[top] = typeTotal.GetValueOrDefault(top) + 1;

            if (missingMpn)
            {
                typeNoMpn[top] = typeNoMpn.GetValueOrDefault(top) + 1;
            }
        }

        var topTypes = typeTotal
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new OverviewTypeLine(pair.Key, pair.Value, typeNoMpn.GetValueOrDefault(pair.Key)))
            .ToList();

        return new DocumentTally(count, withoutType, folders, new OverviewGaps(noMpn, noLcsc, noFootprint), topTypes);
    }
}
