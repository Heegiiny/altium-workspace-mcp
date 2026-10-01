using AltiumWorkspaceMCP.Vault.Rest;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>A "parameter = value" pair by which a duplicate is searched.</summary>
public sealed record DuplicateKey(string Parameter, string Value);

/// <summary>A part that already has such a value.</summary>
public sealed record ExistingPart(string Hrid, string? Comment, string? Folder);

/// <summary>A parameter value that other parts already have.</summary>
public sealed record DuplicateHit(string Parameter, string Value, IReadOnlyList<ExistingPart> Existing);

/// <summary>A row of one call (a copy or an edit) with its value pairs for the check of duplicates inside one call.</summary>
public sealed record CallRow(string RowId, IReadOnlyList<DuplicateKey> Keys);

/// <summary>A parameter value match between rows of one call (without calling the search service).</summary>
public sealed record WithinCallHit(string Parameter, string Value, IReadOnlyList<string> RowIds);

/// <summary>
/// Protection against duplicates by manufacturer number: one search service request for all
/// "parameter = value" pairs for <c>Manufacturer Part Number</c> and <c>LCSC Part#</c>.
/// </summary>
/// <remarks>
/// The match is strict, case-insensitive, on the whole value. The search index is updated with a delay:
/// a just created part may not be found yet, so the check catches duplicates of earlier parts,
/// not of parts created a second ago.
/// </remarks>
public sealed class DuplicateFinder
{
    /// <summary>Parameters by which duplicates are searched.</summary>
    public static readonly IReadOnlyList<string> Parameters = ["Manufacturer Part Number", "LCSC Part#"];

    /// <summary>How many documents are read per request: a few parts per value.</summary>
    public const int SearchLimit = 200;

    /// <summary>An explanation of the index delay — for the response and the tool descriptions.</summary>
    public const string IndexNote =
        "The search index is updated with a delay: a part created seconds ago may not be found yet, "
        + "so the check is guaranteed to see only earlier parts.";

    private readonly SearchClient _search;
    private readonly VaultCatalog _catalog;

    public DuplicateFinder(SearchClient search, VaultCatalog catalog)
    {
        _search = search;
        _catalog = catalog;
    }

    /// <summary>An empty value and dashes are not searched: for such parts the value is "not set", not "the same".</summary>
    public static bool IsSearchable(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Any(symbol => !char.IsWhiteSpace(symbol) && symbol is not ('-' or '–' or '—' or '−' or '_'));

    /// <summary>Pairs for searching by the part's parameters: only <see cref="Parameters"/>, values without edge spaces.</summary>
    public static IReadOnlyList<DuplicateKey> Keys(IReadOnlyDictionary<string, string> parameters)
    {
        var keys = new List<DuplicateKey>();

        foreach (string name in Parameters)
        {
            string? value = parameters
                .FirstOrDefault(entry => string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

            if (IsSearchable(value))
            {
                keys.Add(new DuplicateKey(name, value!.Trim()));
            }
        }

        return keys;
    }

    /// <summary>
    /// Copy parameters: the sample's values, with the ones set in the copy over them (including an empty one, which
    /// removes the sample's value). An inherited MPN is the main case of a duplicate.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Effective(
        IReadOnlyDictionary<string, string> source,
        IReadOnlyDictionary<string, string> overrides)
    {
        var result = new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);

        foreach ((string name, string value) in overrides)
        {
            result[name] = value;
        }

        return result;
    }

    /// <summary>
    /// The search condition: components (as in the panel) and any of the pairs — <c>Should</c> over strict matches.
    /// Identical pairs (case-insensitive) are merged. Without pairs the condition is empty — there is nothing to search.
    /// </summary>
    public static BooleanCondition BuildCondition(IEnumerable<DuplicateKey> keys, IEnumerable<string>? excludedStates = null)
    {
        BooleanCondition any = BooleanCondition.Empty;

        foreach (DuplicateKey key in Distinct(keys))
        {
            any = any.With(new StrictCondition(SearchFieldNames.Parameter(key.Parameter), key.Value), SearchOccur.Should);
        }

        return any.Items.Count == 0
            ? any
            : SearchClient.ComponentsBase(excludedStates).With(any);
    }

    /// <summary>Fields needed from a document: identifier, name, folder and the parameters themselves.</summary>
    public static IReadOnlyList<string> ReturnFields() =>
    [
        SearchFieldNames.Parameter("ItemHRID"),
        SearchFieldNames.Core("Comment"),
        SearchFieldNames.Core("FolderFullPath"),
        .. Parameters.Select(SearchFieldNames.Parameter),
    ];

    /// <summary>
    /// Distributes the found documents over the pairs. <paramref name="exclude"/> — parts that are not
    /// counted as a duplicate (an edit of the part itself).
    /// </summary>
    public static IReadOnlyList<DuplicateHit> Match(
        IEnumerable<DuplicateKey> keys,
        IEnumerable<SearchDocument> documents,
        IReadOnlyCollection<string>? exclude = null)
    {
        var docs = documents.ToList();
        var hits = new List<DuplicateHit>();

        foreach (DuplicateKey key in Distinct(keys))
        {
            string field = SearchFieldNames.Parameter(key.Parameter);

            var existing = docs
                .Where(doc => string.Equals(doc.Get(field)?.Trim(), key.Value, StringComparison.OrdinalIgnoreCase))
                .Select(doc => new ExistingPart(
                    doc.Get(SearchFieldNames.Parameter("ItemHRID")) ?? "?",
                    doc.Get(SearchFieldNames.Core("Comment")),
                    doc.Get(SearchFieldNames.Core("FolderFullPath"))?.TrimEnd('\\')))
                .Where(part => exclude is null || !exclude.Contains(part.Hrid, StringComparer.OrdinalIgnoreCase))
                .DistinctBy(part => part.Hrid, StringComparer.OrdinalIgnoreCase)
                .OrderBy(part => part.Hrid, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (existing.Count > 0)
            {
                hits.Add(new DuplicateHit(key.Parameter, key.Value, existing));
            }
        }

        return hits;
    }

    /// <summary>Looks in the vault for parts with these values; hidden lifecycle states do not count.</summary>
    public async Task<IReadOnlyList<DuplicateHit>> FindAsync(
        IEnumerable<DuplicateKey> keys,
        IReadOnlyCollection<string>? exclude,
        CancellationToken cancellationToken)
    {
        var distinct = Distinct(keys).ToList();

        if (distinct.Count == 0)
        {
            return [];
        }

        var hidden = FootprintPanel.HiddenStates((await _catalog.GetLifeCycleStatesAsync(cancellationToken)).Values);

        SearchResponse response = await _search.SearchAsync(
            new SearchRequest
            {
                Condition = BuildCondition(distinct, hidden.Select(state => state.GUID)),
                Limit = SearchLimit,
                ReturnFields = ReturnFields(),
            },
            cancellationToken);

        return Match(distinct, response.Documents, exclude);
    }

    private static IEnumerable<DuplicateKey> Distinct(IEnumerable<DuplicateKey> keys) =>
        keys.DistinctBy(key => (key.Parameter.ToLowerInvariant(), key.Value.ToLowerInvariant()));

    /// <summary>
    /// Duplicates inside one call: a parameter value set in more than one row
    /// (copies or edits). No network is used — only the passed rows are compared, so the check
    /// also catches what the search index does not see yet (a just invented MPN in two copies at once).
    /// The comparison is like the search's: case-insensitive, by the trimmed value (<see cref="Keys"/> already does that).
    /// One and the same row with one and the same value (for example, MPN and LCSC Part# matched in the row itself)
    /// counts once.
    /// </summary>
    public static IReadOnlyList<WithinCallHit> WithinCall(IEnumerable<CallRow> rows)
    {
        var groups = new Dictionary<(string Parameter, string Value), (string Parameter, string Value, List<string> RowIds)>();

        foreach (CallRow row in rows)
        {
            var seenInRow = new HashSet<(string Parameter, string Value)>();

            foreach (DuplicateKey key in row.Keys)
            {
                var normalized = (Parameter: key.Parameter.ToLowerInvariant(), Value: key.Value.ToLowerInvariant());

                if (!seenInRow.Add(normalized))
                {
                    continue;
                }

                if (!groups.TryGetValue(normalized, out var group))
                {
                    group = (key.Parameter, key.Value, []);
                    groups[normalized] = group;
                }

                group.RowIds.Add(row.RowId);
            }
        }

        return groups.Values
            .Where(group => group.RowIds.Count > 1)
            .Select(group => new WithinCallHit(group.Parameter, group.Value, group.RowIds))
            .ToList();
    }
}
