using System.ComponentModel;
using AltiumWorkspaceMCP.Vault;
using ModelContextProtocol.Server;

namespace AltiumWorkspaceMCP.Tools;

/// <summary>
/// Analog of the Components panel in Altium Designer: the type tree, type parameters and a part table
/// by parametric filters. Works on the Workspace search index, so it is fast over the whole vault.
/// </summary>
[McpServerToolType]
public sealed class ComponentPanelTools
{
    /// <summary>The search service returns at most this many distinct values per parameter.</summary>
    private const int FacetValuesLimit = 1000;

    private readonly VaultWorkspace _workspace;

    public ComponentPanelTools(VaultWorkspace workspace) => _workspace = workspace;

    [McpServerTool(Name = "vault_components", ReadOnly = true)]
    [Description("""
        The main way to learn what is in the vault: types → facets → list. Works on the Workspace
        search index, fast over the whole vault (seconds instead of walking folders).

        Actions:
          types  — tree of component types with part counts (default). under — a tree
                   branch; without under the two top levels are shown. The line "[+N]" — how many
                   nested types with parts are hidden.
          facets — parameters of the chosen type and their values with part counts (the 15 most
                   frequent each), for numeric ones — the range. Shows what can go into filters.
          overview — summary by type (or by the whole vault): part count, folders (top 10), gaps
                   (no MPN, no LCSC Part#, no footprint), type parameters with
                   fill rate and top 5 values; without type — top types and the share without MPN.
                   Selection — type, folder, footprint, filters. The response is at most 8 000 characters.
          list   — parts as a table in the vault_table format (hrid, folder, revision, comment,
                   description, the footprint column and parameter columns), total —
                   how many were found in all, nextOffset — where to continue. compact=true —
                   a row has only hrid and the requested columns (columns, including footprint and
                   footprintRevision), without folder, revision, comment, description: a row is
                   shorter, more rows fit the response budget per call.

        Type (type) — a path in the type tree, its ending or a name: "Resistors Small",
        "Passive\Resistors"; parts of the type and all nested ones are selected. Filters (filters) —
        strings "Name=Value", "Name>=Value", "Name<=Value": Value=10k, Case/Package=0603,
        Voltage Rating>=16. Numeric parameter values are compared by number, with prefixes
        and units (10k, 4.7u, 100nF); text ones — whole, case-insensitive, * and ?
        are allowed. text — words separated by spaces, each is searched in the part text and description as a substring.

        Folder (folder) — a path or its ending, "Passive\Resistors": parts of the folder and
        all nested ones are selected; if the path was matched loosely, the response has resolvedFolder. Footprint
        (footprint) — the whole name, "R 0603", case does not matter. In list the footprint column is
        the part's current footprint (for several — comma-separated with the number in []); with
        includeGuids=true footprintRevision is added — a revision like "PCC-0009-3"
        (footprint HRID and revision number); vault_set_links accepts a target HRID without
        the revision number ("PCC-0009") and takes the active revision. For old parts the index
        does not store the revision (empty) — then look at vault_component_detail. In facets — a summary
        footprints: how many parts use which footprint and how many have none.

        As in the Components panel, parts in the states Obsolete, Abandoned,
        Deleted are not shown (excludedStates); includeAllStates=true returns them too.

        Important: values in facets are shown in lower case — that is how the index stores them; in filters case
        does not matter. The search index is updated with a delay: look for a just created or changed
        part through vault_table. Open a part from the listing with vault_component_detail.
        """)]
    public async Task<object> QueryAsync(
        [Description("types, facets, overview or list.")]
        string action = "types",
        [Description("""
            Component type for facets and list: a path, its ending or a name, for example
            "Passive\Resistors\Resistors Small" or "Resistors Small". Empty — the whole vault.
            """)]
        string? type = null,
        [Description("For types: a type tree branch, for example 'Passive'. Empty — the top of the tree.")]
        string? under = null,
        [Description("""
            Filters for facets and list: "Value=10k", "Case/Package=0603", "Voltage Rating>=16",
            "Tolerance<=5". All filters apply together (AND).
            """)]
        string[]? filters = null,
        [Description("Words to search in the part text, separated by spaces, for example '10k 0603'. Only for list.")]
        string? text = null,
        [Description("""
            Folder for facets and list: a path, its ending or a GUID, for example "Passive\Resistors\0603".
            Parts of the folder and nested folders are selected. Empty — the whole vault.
            """)]
        string? folder = null,
        [Description("""
            Footprint for facets and list: the whole name, for example "R 0603" (case does not matter;
            searched among all footprints of the part). On a typo the response suggests similar names.
            """)]
        string? footprint = null,
        [Description("""
            For list: which parameters to show as columns, for example ["Value","Case/Package"].
            The columns footprint and footprintRevision — the footprint and its revision. Without columns
            footprint is always shown; with columns — only if listed.
            For facets: which parameters to show values for. Empty — the most frequent.
            """)]
        string[]? columns = null,
        [Description("For list: add the footprintRevision column — the footprint revision ('PCC-0009-3').")]
        bool includeGuids = false,
        [Description("""
            For list: only hrid and the requested columns in a row, without folder, revision, comment,
            description — a shorter row, more rows per call.
            """)]
        bool compact = false,
        [Description("Do not hide the states Obsolete, Abandoned, Deleted that the Components panel hides.")]
        bool includeAllStates = false,
        [Description("For list: how many rows to return (default 50, at most 200).")]
        int limit = 50,
        [Description("For list: how many rows to skip; take nextOffset from the previous response.")]
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        ComponentPanelService panel = _workspace.Panel;
        string mode = action.Trim().ToLowerInvariant();

        return mode switch
        {
            "types" => await TypesAsync(panel, under ?? type, includeAllStates, cancellationToken),
            "facets" => await FacetsAsync(
                panel, new PanelQuery(type, filters, null, folder, footprint, includeAllStates), columns, cancellationToken),
            "overview" => await OverviewAsync(
                panel, new PanelQuery(type, filters, text, folder, footprint, includeAllStates), cancellationToken),
            "list" => await ListAsync(
                panel, new PanelQuery(type, filters, text, folder, footprint, includeAllStates), columns, includeGuids, compact, limit, offset, cancellationToken),
            _ => throw new ArgumentException($"Unknown action '{action}'. Allowed: types, facets, overview, list."),
        };
    }

    /// <summary>Hidden states for the response: empty if includeAllStates disabled the exclusion.</summary>
    private static string? ExcludedStates(ScopeInfo scope) =>
        scope.ExcludedStates.Count > 0 ? string.Join(", ", scope.ExcludedStates) : null;

    private async Task<object> TypesAsync(ComponentPanelService panel, string? under, bool includeAllStates, CancellationToken cancellationToken)
    {
        TypeTreeResult result = await panel.GetTypesAsync(under, includeAllStates, cancellationToken);

        var tree = result.Lines
            .Select(line => $"{line.Path} — {line.Total}"
                + (line.Own != line.Total ? $" (own {line.Own})" : string.Empty)
                + (line.Hidden > 0 ? $" [+{line.Hidden}]" : string.Empty))
            .ToList();

        string hint = result.Under is null
            ? "Next: vault_components action=facets type=<path> — type parameters and their values; action=list — parts."
            : $"Nested types deeper than shown: vault_components action=types under=<path>. Parts of the type: action=list type={result.Under}.";

        return new
        {
            total = result.Total,
            withoutType = result.WithoutType,
            under = result.Under,
            excludedStates = result.ExcludedStates.Count > 0 ? string.Join(", ", result.ExcludedStates) : null,
            tree,
            hint = result.WithoutType > 0
                ? $"{hint} Parts without a type: {result.WithoutType} (vault_check_components shows them, vault_component_types with assign sets the type)."
                : hint,
        };
    }

    private async Task<object> FacetsAsync(
        ComponentPanelService panel,
        PanelQuery query,
        string[]? columns,
        CancellationToken cancellationToken)
    {
        FacetsResult result = await panel.GetFacetsAsync(query, columns ?? [], cancellationToken);

        // Parameters are taken while they fit the response budget; the footprint summary is in the reserve.
        var budget = new ResponseBudget(_workspace.Options.MaxResponseChars, 3500);
        var parameters = budget.TakeFitting(result.Parameters);
        int cut = result.Parameters.Count - parameters.Count;
        var hints = new List<string>();

        if (result.HiddenParameters > 0)
        {
            hints.Add($"Shown {result.Parameters.Count} most filled parameters of {result.Parameters.Count + result.HiddenParameters}; "
                + $"the others: {string.Join(", ", result.HiddenNames)}. List the needed parameter in columns.");
        }

        if (cut > 0)
        {
            hints.Add($"The response is limited by the budget: {cut} parameters are not shown — list the needed ones in columns.");
        }

        if (result.Total == 0)
        {
            hints.Add("Nothing found: loosen filters or check type (vault_components action=types) and folder.");
        }

        if (result.FootprintSuggestions.Count > 0)
        {
            hints.Add($"No footprint '{query.Footprint}'. Similar: {string.Join("; ", result.FootprintSuggestions)}.");
        }

        hints.Add("Values are shown in lower case, as in the index; in filters case does not matter. Next: action=list with the same type, folder, footprint and filters.");

        return new
        {
            type = result.Scope.Type,
            folder = result.Scope.Folder,
            resolvedFolder = result.Scope.FolderMatch?.Describe(query.Folder ?? string.Empty),
            excludedStates = ExcludedStates(result.Scope),
            total = result.Total,
            footprints = result.Footprints is { } footprints
                ? new
                {
                    withFootprint = footprints.With,
                    withoutFootprint = footprints.Without,
                    valuesTotal = footprints.ValuesTotal,
                    values = footprints.Values.Concat(
                        footprints.ValuesTotal > footprints.Values.Count ? [$"[+{footprints.ValuesTotal - footprints.Values.Count}]"] : []),
                }
                : null,
            parameters = parameters.Select(parameter => new
            {
                name = parameter.Name,
                filled = parameter.Filled,
                numeric = parameter.Numeric ? true : (bool?)null,
                range = parameter.Range,
                valuesTotal = parameter.ValuesTotal >= FacetValuesLimit ? $"{FacetValuesLimit}+" : parameter.ValuesTotal.ToString(),
                values = parameter.Values,
            }),
            hint = string.Join(" ", hints),
        };
    }

    /// <summary>Limit of the <c>overview</c> response, in characters.</summary>
    private const int OverviewChars = 8000;

    private async Task<object> OverviewAsync(ComponentPanelService panel, PanelQuery query, CancellationToken cancellationToken)
    {
        OverviewResult result = await panel.GetOverviewAsync(query, cancellationToken);
        return OverviewReport.Build(result, query, Math.Min(_workspace.Options.MaxResponseChars, OverviewChars));
    }

    private async Task<object> ListAsync(
        ComponentPanelService panel,
        PanelQuery query,
        string[]? columns,
        bool includeGuids,
        bool compact,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        ListResult result = await panel.ListAsync(query, columns ?? [], includeGuids, compact, limit, offset, cancellationToken);

        int reserved = ResponseBudget.SizeOf(result.Columns) + ResponseBudget.SizeOf(result.OtherParameters) + 1500;
        var budget = new ResponseBudget(_workspace.Options.MaxResponseChars, reserved);
        var rows = budget.TakeFitting(result.Rows);

        bool cut = rows.Count < result.Rows.Count;
        long shownEnd = result.Offset + rows.Count;
        int? nextOffset = shownEnd < result.Total ? (int)shownEnd : null;
        var hints = new List<string>();

        if (cut)
        {
            hints.Add($"The response is limited by a budget of {_workspace.Options.MaxResponseChars} characters: shown {rows.Count} of {result.Rows.Count} rows of the portion. "
                + $"To continue: offset={nextOffset}; a shorter columns list gives fewer characters.");
        }
        else if (nextOffset is not null)
        {
            hints.Add($"There are more rows: continue with offset={nextOffset}.");
        }

        if (result.Total == 0)
        {
            hints.Add("Nothing found. Loosen filters, text, folder or footprint; vault_components action=facets shows the parameters, values "
                + "and footprints that the selection has. "
                + "The search index is updated with a delay: look for a just created part with vault_table.");
        }

        if (result.FootprintSuggestions.Count > 0)
        {
            hints.Add($"No footprint '{query.Footprint}'. Similar: {string.Join("; ", result.FootprintSuggestions)}.");
        }

        if (result.WithoutRevision > 0)
        {
            hints.Add($"For {result.WithoutRevision} rows the index does not store the footprint revision (old parts): "
                + "take it from vault_component_detail (models).");
        }

        if (result.OtherParameters.Count > 0)
        {
            hints.Add($"Other parameters of the found parts: {string.Join(", ", result.OtherParameters)}. List the needed ones in columns.");
        }

        return new
        {
            type = result.Scope.Type,
            folder = result.Scope.Folder,
            resolvedFolder = result.Scope.FolderMatch?.Describe(query.Folder ?? string.Empty),
            excludedStates = ExcludedStates(result.Scope),
            total = result.Total,
            count = rows.Count,
            offset = result.Offset,
            nextOffset,
            elapsedMs = result.ElapsedMs,
            hint = hints.Count > 0 ? string.Join(" ", hints) : null,
            columns = result.Columns,
            rows,
        };
    }
}
