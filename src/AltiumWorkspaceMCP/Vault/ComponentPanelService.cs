using System.Diagnostics;
using AltiumWorkspaceMCP.Soap.Vault;
using AltiumWorkspaceMCP.Vault.Rest;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>A panel request: type, folder, footprint, parameter filters and text.</summary>
/// <param name="IncludeAllStates">Do not exclude the lifecycle states that the Components panel hides.</param>
public sealed record PanelQuery(
    string? Type = null,
    IReadOnlyList<string>? Filters = null,
    string? Text = null,
    string? Folder = null,
    string? Footprint = null,
    bool IncludeAllStates = false)
{
    public IReadOnlyList<string> FilterList => Filters ?? [];
}

/// <summary>The type tree with part counts.</summary>
public sealed record TypeTreeResult(
    long Total,
    long WithoutType,
    string? Under,
    IReadOnlyList<TypeLine> Lines,
    IReadOnlyList<string> ExcludedStates);

/// <summary>A parameter and its values in the selection.</summary>
public sealed record ParameterFacetView(
    string Name,
    int Filled,
    IReadOnlyList<string> Values,
    int ValuesTotal,
    bool Numeric,
    string? Range);

/// <summary>Footprint summary of the selection: how many parts have one and the most frequent names.</summary>
/// <param name="With">Parts with a footprint (the <c>FootprintName1</c> field).</param>
/// <param name="Without">Parts without a footprint.</param>
/// <param name="Values">The most frequent names with part counts (lower case, as in the index).</param>
/// <param name="ValuesTotal">How many distinct names there are in all.</param>
public sealed record FootprintSummary(int With, long Without, IReadOnlyList<string> Values, int ValuesTotal);

/// <summary>What is really selected: the type, the folder (marked if it was found loosely) and the hidden states.</summary>
public sealed record ScopeInfo(string? Type, string? Folder, FolderMatch? FolderMatch, IReadOnlyList<string> ExcludedStates);

/// <summary>Result of <c>facets</c>: the selection parameters and their values.</summary>
public sealed record FacetsResult(
    ScopeInfo Scope,
    long Total,
    IReadOnlyList<ParameterFacetView> Parameters,
    int HiddenParameters,
    IReadOnlyList<string> HiddenNames,
    FootprintSummary? Footprints,
    IReadOnlyList<string> FootprintSuggestions);

/// <summary>Result of <c>list</c>: table rows in the vault_table format.</summary>
/// <param name="WithoutRevision">Rows that have a footprint but the index does not store its revision.</param>
public sealed record ListResult(
    ScopeInfo Scope,
    long Total,
    int Offset,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    IReadOnlyList<string> OtherParameters,
    long ElapsedMs,
    int WithoutRevision,
    IReadOnlyList<string> FootprintSuggestions);

/// <summary>
/// An analog of the Components panel on the workspace search service: the type tree, type parameters
/// with frequent values and a part table by parametric filters.
/// </summary>
public sealed class ComponentPanelService
{
    /// <summary>How many parameter values <c>facets</c> shows.</summary>
    public const int TopValues = 15;

    /// <summary>How many parameters <c>facets</c> shows if no columns are set.</summary>
    public const int DefaultParameters = 25;

    /// <summary>How many parameter columns <c>list</c> takes if no columns are set.</summary>
    public const int DefaultColumns = 8;

    public const int MaxLimit = 200;

    private static readonly TimeSpan NodesLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ParametersLifetime = TimeSpan.FromMinutes(5);

    private readonly SearchClient _search;
    private readonly ComponentTypeService _types;
    private readonly VaultCatalog _catalog;
    private readonly object _cacheGate = new();
    private (DateTime At, IReadOnlyList<ComponentTypeNode> Nodes)? _nodes;
    private readonly Dictionary<string, (DateTime At, PanelParameters Parameters)> _parameters = new(StringComparer.Ordinal);

    public ComponentPanelService(SearchClient search, ComponentTypeService types, VaultCatalog catalog)
    {
        _search = search;
        _types = types;
        _catalog = catalog;
    }

    /// <summary>The selected area: type, folder and lifecycle states; a condition without filters, text and footprint.</summary>
    private sealed record Scope(
        ComponentTypeNode? Node,
        FolderMatch? Folder,
        IReadOnlyList<string> ExcludedStates,
        BooleanCondition Condition,
        string Key)
    {
        public ScopeInfo Info => new(Node?.Path, Folder?.Folder.Path, Folder, ExcludedStates);
    }

    // ── Types ─────────────────────────────────────────────────────────────────

    /// <summary>The type tree: without <paramref name="under"/> — two top levels, with it — the type and two levels under it.</summary>
    public async Task<TypeTreeResult> GetTypesAsync(string? under, bool includeAllStates, CancellationToken cancellationToken)
    {
        IReadOnlyList<ComponentTypeNode> nodes = await GetNodesAsync(cancellationToken);
        ComponentTypeNode? root = string.IsNullOrWhiteSpace(under) ? null : ComponentTypeTree.Resolve(nodes, under);
        Scope scope = await BuildScopeAsync(new PanelQuery(IncludeAllStates: includeAllStates), cancellationToken);

        SearchResponse response = await _search.SearchAsync(
            new SearchRequest { Condition = scope.Condition, Limit = 0, IncludeFacets = true },
            cancellationToken);

        SearchFacet? facet = response.Facets.FirstOrDefault(item => item.Name == SearchFieldNames.Parameter("ComponentType"));
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (FacetCounter counter in facet?.Counters ?? [])
        {
            counts[ComponentTypeTree.Key(counter.Value)] = counter.Count;
        }

        long withType = counts.Values.Sum(count => (long)count);
        var lines = ComponentTypeTree.Build(nodes, counts, root?.Path, depth: 2);

        return new TypeTreeResult(response.Total, response.Total - withType, root?.Path, lines, scope.ExcludedStates);
    }

    /// <summary>Part count by type in all lifecycle states: the key is <see cref="ComponentTypeTree.Key"/> of the path.</summary>
    public async Task<IReadOnlyDictionary<string, int>> GetTypeCountsAsync(CancellationToken cancellationToken)
    {
        Scope scope = await BuildScopeAsync(new PanelQuery(IncludeAllStates: true), cancellationToken);

        SearchResponse response = await _search.SearchAsync(
            new SearchRequest { Condition = scope.Condition, Limit = 0, IncludeFacets = true },
            cancellationToken);

        SearchFacet? facet = response.Facets.FirstOrDefault(item => item.Name == SearchFieldNames.Parameter("ComponentType"));
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (FacetCounter counter in facet?.Counters ?? [])
        {
            counts[ComponentTypeTree.Key(counter.Value)] = counter.Count;
        }

        return counts;
    }

    // ── Parameters and values ─────────────────────────────────────────────────

    /// <summary>
    /// Selection parameters (type, folder, filters) and their values with part counts, and the footprint summary.
    /// </summary>
    public async Task<FacetsResult> GetFacetsAsync(
        PanelQuery query,
        IReadOnlyList<string> columns,
        CancellationToken cancellationToken)
    {
        Scope scope = await BuildScopeAsync(query, cancellationToken);
        BooleanCondition condition = await BuildConditionAsync(scope, query, cancellationToken);

        SearchResponse response = await _search.SearchAsync(
            new SearchRequest { Condition = condition, Limit = 0, IncludeFacets = true },
            cancellationToken);

        PanelParameters parameters = PanelParameters.FromFacets(response.Facets);
        IEnumerable<PanelParameter> selected;
        int hidden = 0;
        IReadOnlyList<string> hiddenNames = [];

        if (columns.Count > 0)
        {
            selected = columns.Select(parameters.Find).DistinctBy(parameter => parameter.Name).ToList();
        }
        else
        {
            selected = parameters.All.Take(DefaultParameters).ToList();
            hidden = Math.Max(parameters.All.Count - DefaultParameters, 0);
            hiddenNames = parameters.All.Skip(DefaultParameters).Take(30).Select(parameter => parameter.Name).ToList();
        }

        var views = selected.Select(View).ToList();
        IReadOnlyList<string> suggestions = response.Total == 0
            ? await SuggestFootprintsAsync(scope, query, cancellationToken)
            : [];

        return new FacetsResult(
            scope.Info, response.Total, views, hidden, hiddenNames, SummarizeFootprints(response), suggestions);
    }

    /// <summary>How many documents are read per summary request.</summary>
    private const int OverviewPage = 5000;

    /// <summary>The summary reads no more than this many documents (the shares are then approximate).</summary>
    private const int OverviewMaxDocuments = 20000;

    /// <summary>
    /// Summary of a selection: part count, folders, gaps (MPN, LCSC Part#, footprint) and, if a type is set,
    /// the fill rate of its parameters. Two requests: facets (fill rate and values) and documents with five fields
    /// (folder, type, MPN, LCSC, footprint) — the index has neither a folder facet nor a template field.
    /// </summary>
    public async Task<OverviewResult> GetOverviewAsync(PanelQuery query, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        Scope scope = await BuildScopeAsync(query, cancellationToken);
        BooleanCondition condition = await BuildConditionAsync(scope, query, cancellationToken);
        bool withParameters = scope.Node is not null;

        Task<SearchResponse> facets = withParameters
            ? _search.SearchAsync(new SearchRequest { Condition = condition, Limit = 0, IncludeFacets = true }, cancellationToken)
            : Task.FromResult(new SearchResponse(0, [], []));

        var documents = new List<SearchDocument>();
        long total = 0;

        for (int start = 0; start < OverviewMaxDocuments; start += OverviewPage)
        {
            SearchResponse page = await _search.SearchAsync(
                new SearchRequest
                {
                    Condition = condition,
                    Start = start,
                    Limit = OverviewPage,
                    ReturnFields = TypeOverview.ReturnFields,
                    Sort = [new SearchSort(SearchFieldNames.Parameter("ItemHRID"), Descending: false)],
                },
                cancellationToken);

            total = page.Total;
            documents.AddRange(page.Documents);

            if (page.Documents.Count < OverviewPage || documents.Count >= total)
            {
                break;
            }
        }

        SearchResponse facetsResponse = await facets;
        var parameters = withParameters ? TypeOverview.FromFacets(facetsResponse.Facets) : [];

        watch.Stop();
        return new OverviewResult(
            scope.Info, total, TypeOverview.Tally(documents), parameters, documents.Count < total, watch.ElapsedMilliseconds);
    }

    /// <summary>The <c>FootprintName1</c> facet of the result: "how many parts use which footprint", top 15.</summary>
    private static FootprintSummary? SummarizeFootprints(SearchResponse response)
    {
        SearchFacet? facet = response.Facets.FirstOrDefault(item => item.Name == SearchFieldNames.FootprintName(1));

        if (facet is null)
        {
            return response.Total == 0 ? new FootprintSummary(0, 0, [], 0) : null;
        }

        return new FootprintSummary(
            facet.TotalHitCount,
            Math.Max(response.Total - facet.TotalHitCount, 0),
            facet.Counters.OrderByDescending(counter => counter.Count).Take(TopValues).Select(counter => $"{counter.Value} ({counter.Count})").ToList(),
            facet.Counters.Count);
    }

    /// <summary>
    /// A footprint selection found nothing: the nearest names among the footprints of the selection without this filter
    /// (values from the facets — in lower case; case does not matter in the selection).
    /// </summary>
    private async Task<IReadOnlyList<string>> SuggestFootprintsAsync(Scope scope, PanelQuery query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.Footprint))
        {
            return [];
        }

        SearchResponse response = await _search.SearchAsync(
            new SearchRequest { Condition = scope.Condition, Limit = 0, IncludeFacets = true },
            cancellationToken);

        var candidates = response.Facets
            .Where(facet => FootprintPanel.IsNameFacet(facet.Name))
            .SelectMany(facet => facet.Counters.Select(counter => counter.Value))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return NameSuggester.Nearest(query.Footprint.Trim(), candidates);
    }

    private static ParameterFacetView View(PanelParameter parameter)
    {
        // Values — as the index returns them (lower case); for a numeric parameter the displayed text is taken.
        SearchFacet? source = parameter.TextFacet ?? parameter.NumericFacet;
        IReadOnlyList<FacetCounter> counters = source?.Counters ?? [];

        string? range = parameter.NumericFacet is { SupportsRange: true, MinValue: not null, MaxValue: not null } numeric
            ? $"{numeric.MinValue} … {numeric.MaxValue}"
                + (ParameterValueCodec.UnitOf(parameter.TypeGuid) is { Length: > 0 } unit ? $" {unit}" : string.Empty)
            : null;

        return new ParameterFacetView(
            parameter.Name,
            parameter.Filled,
            counters.OrderByDescending(counter => counter.Count).Take(TopValues).Select(counter => $"{counter.Value} ({counter.Count})").ToList(),
            counters.Count,
            parameter.IsNumeric,
            range);
    }

    // ── Part table ────────────────────────────────────────────────────────────

    /// <summary>
    /// Parts by type, folder, footprint, filters and text in the vault_table table format. The columns
    /// <c>footprint</c> and <c>footprintRevision</c> are virtual: they can be listed in <paramref name="columns"/>;
    /// without explicit columns the footprint is always shown. <paramref name="compact"/> removes the attributes
    /// <c>folder</c>, <c>revision</c>, <c>comment</c>, <c>description</c> from the row — only
    /// <c>hrid</c> and the requested columns remain, the row is shorter, and more rows fit the response budget.
    /// </summary>
    public async Task<ListResult> ListAsync(
        PanelQuery query,
        IReadOnlyList<string> columns,
        bool includeGuids,
        bool compact,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        limit = Math.Clamp(limit, 1, MaxLimit);
        offset = Math.Max(offset, 0);

        Scope scope = await BuildScopeAsync(query, cancellationToken);
        BooleanCondition condition = await BuildConditionAsync(scope, query, cancellationToken);

        bool explicitColumns = columns.Count > 0;
        bool wantFootprint = !explicitColumns || columns.Any(column => FootprintPanel.IsColumn(column) && !FootprintPanel.IsRevisionColumn(column));
        bool wantRevision = includeGuids || columns.Any(FootprintPanel.IsRevisionColumn);
        var virtualColumns = new List<string>();

        if (wantFootprint)
        {
            virtualColumns.Add(FootprintPanel.ColumnName);
        }

        if (wantRevision)
        {
            virtualColumns.Add(FootprintPanel.RevisionColumnName);
        }

        List<PanelParameter>? requested = null;
        if (explicitColumns)
        {
            var parameterColumns = columns.Where(column => !FootprintPanel.IsColumn(column)).ToList();
            requested = [];

            if (parameterColumns.Count > 0)
            {
                PanelParameters known = await GetParametersAsync(scope, cancellationToken);
                requested = parameterColumns.Select(known.Find).DistinctBy(parameter => parameter.Name).ToList();
            }
        }

        string hrid = SearchFieldNames.Parameter("ItemHRID");
        string folder = SearchFieldNames.Core("FolderFullPath");
        string revision = SearchFieldNames.Core("RevisionId");
        string comment = SearchFieldNames.Core("Comment");
        string description = SearchFieldNames.Core("Description");

        var returnFields = new List<string>();
        if (requested is not null)
        {
            returnFields.AddRange([hrid, folder, revision, comment, description]);
            returnFields.AddRange(FootprintPanel.ReturnFields(wantFootprint, wantRevision));
            returnFields.AddRange(requested.Select(parameter => parameter.TextField ?? parameter.NumericField!));
        }

        SearchResponse response = await _search.SearchAsync(
            new SearchRequest
            {
                Condition = condition,
                Start = offset,
                Limit = limit,
                ReturnFields = returnFields,
                Sort = string.IsNullOrWhiteSpace(query.Text) ? [new SearchSort(hrid, Descending: false)] : [],
            },
            cancellationToken);

        IReadOnlyList<string> names;
        IReadOnlyList<string> other = [];
        var fieldByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (requested is not null)
        {
            names = requested.Select(parameter => parameter.Name).ToList();
            foreach (PanelParameter parameter in requested)
            {
                fieldByName[parameter.Name] = parameter.TextField ?? parameter.NumericField!;
            }
        }
        else
        {
            (names, other) = ChooseColumns(response.Documents, fieldByName);
        }

        IReadOnlyList<string> fixedColumns = FixedColumns(compact);
        var columnNames = fixedColumns.Concat(virtualColumns).Concat(names).ToList();
        int withoutRevision = 0;
        var rows = response.Documents
            .Select(document =>
            {
                var row = new List<string?>(columnNames.Count) { document.Get(hrid) };

                if (!compact)
                {
                    row.Add(FootprintPanel.TrimFolder(document.Get(folder)));
                    row.Add(document.Get(revision));
                    row.Add(document.Get(comment));
                    row.Add(document.Get(description));
                }

                FootprintInfo footprints = wantFootprint || wantRevision ? FootprintPanel.Read(document) : FootprintInfo.Empty;

                if (wantFootprint)
                {
                    row.Add(FootprintPanel.FormatNames(footprints));
                }

                if (wantRevision)
                {
                    row.Add(FootprintPanel.FormatRevisions(footprints));

                    if (footprints.Names.Count > footprints.Revisions.Count)
                    {
                        withoutRevision++;
                    }
                }

                row.AddRange(names.Select(name => NonEmpty(document.Get(fieldByName[name]))));
                return (IReadOnlyList<string?>)row;
            })
            .ToList();

        IReadOnlyList<string> suggestions = response.Total == 0
            ? await SuggestFootprintsAsync(scope, query, cancellationToken)
            : [];

        watch.Stop();
        return new ListResult(
            scope.Info, response.Total, offset, columnNames, rows, other, watch.ElapsedMilliseconds, withoutRevision, suggestions);
    }

    /// <summary>
    /// Columns when they are not set: the parameters that occur most often in the found parts,
    /// at most <see cref="DefaultColumns"/>; the rest — as an "other parameters" list.
    /// </summary>
    private static (IReadOnlyList<string> Names, IReadOnlyList<string> Other) ChooseColumns(
        IReadOnlyList<SearchDocument> documents,
        Dictionary<string, string> fieldByName)
    {
        var frequency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (SearchDocument document in documents)
        {
            foreach ((string field, string value) in document.Fields)
            {
                ParsedFieldName? parsed = SearchFieldNames.Parse(field);

                if (parsed is not { Group: FieldGroup.Parameters, Kind: FieldKind.Plain or FieldKind.TextMirror }
                    || PanelParameters.IsSystemName(parsed.Parameter)
                    || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                fieldByName.TryAdd(parsed.Parameter, field);
                frequency[parsed.Parameter] = frequency.GetValueOrDefault(parsed.Parameter) + 1;
            }
        }

        var ordered = frequency
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key)
            .ToList();

        return (ordered.Take(DefaultColumns).ToList(), ordered.Skip(DefaultColumns).Take(12).ToList());
    }

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>Attribute columns of a <c>list</c> row: the full set or only <c>hrid</c> in <c>compact</c> mode.</summary>
    internal static IReadOnlyList<string> FixedColumns(bool compact) => compact ? ["hrid"] : ComponentTable.FixedColumns;

    // ── Common ────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<ComponentTypeNode>> GetNodesAsync(CancellationToken cancellationToken)
    {
        lock (_cacheGate)
        {
            if (_nodes is { } cached && DateTime.UtcNow - cached.At < NodesLifetime)
            {
                return cached.Nodes;
            }
        }

        IReadOnlyList<ComponentTypeNode> nodes = await _types.ListAsync(cancellationToken);
        lock (_cacheGate)
        {
            _nodes = (DateTime.UtcNow, nodes);
        }

        return nodes;
    }

    /// <summary>The type and paths for selection: the type itself and all nested ones. Without a type — selection over the whole vault.</summary>
    private async Task<(ComponentTypeNode? Node, IReadOnlyList<string> Paths)> ResolveTypeAsync(
        string? type,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return (null, []);
        }

        IReadOnlyList<ComponentTypeNode> nodes = await GetNodesAsync(cancellationToken);
        ComponentTypeNode node = ComponentTypeTree.Resolve(nodes, type);
        return (node, ComponentTypeTree.WithDescendants(nodes, node));
    }

    /// <summary>
    /// The selection area: the type with subtypes, the folder with nested ones (loosely, as in reading) and the lifecycle states
    /// the panel does not show (the list is from the vault, the "not applicable" flag; <c>includeAllStates</c> turns it off).
    /// </summary>
    private async Task<Scope> BuildScopeAsync(PanelQuery query, CancellationToken cancellationToken)
    {
        (ComponentTypeNode? node, IReadOnlyList<string> paths) = await ResolveTypeAsync(query.Type, cancellationToken);

        FolderMatch? folder = string.IsNullOrWhiteSpace(query.Folder)
            ? null
            : await _catalog.ResolveFolderMatchAsync(query.Folder, FolderMatchMode.Lenient, cancellationToken);

        IReadOnlyList<ALU_LifeCycleState> hidden = query.IncludeAllStates
            ? []
            : FootprintPanel.HiddenStates((await _catalog.GetLifeCycleStatesAsync(cancellationToken)).Values);

        BooleanCondition condition = TypeCondition(SearchClient.ComponentsBase(hidden.Select(state => state.GUID)), paths);
        if (folder is not null)
        {
            condition = condition.With(FootprintPanel.FolderCondition(folder.Folder.Path));
        }

        string key = $"{(node is null ? string.Empty : ComponentTypeTree.Key(node.Path))}|{folder?.Folder.Guid}|{(query.IncludeAllStates ? "all" : "panel")}";
        var stateNames = hidden
            .Select(state => state.HRID ?? string.Empty)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new Scope(node, folder, stateNames, condition, key);
    }

    /// <summary>Selection parameters: one request with facets, the result is remembered for five minutes.</summary>
    private async Task<PanelParameters> GetParametersAsync(Scope scope, CancellationToken cancellationToken)
    {
        lock (_cacheGate)
        {
            if (_parameters.TryGetValue(scope.Key, out var cached) && DateTime.UtcNow - cached.At < ParametersLifetime)
            {
                return cached.Parameters;
            }
        }

        SearchResponse response = await _search.SearchAsync(
            new SearchRequest { Condition = scope.Condition, Limit = 0, IncludeFacets = true },
            cancellationToken);

        PanelParameters parameters = PanelParameters.FromFacets(response.Facets);
        lock (_cacheGate)
        {
            _parameters[scope.Key] = (DateTime.UtcNow, parameters);
        }

        return parameters;
    }

    /// <summary>The selection area plus the footprint selection, parameter filters and text.</summary>
    private async Task<BooleanCondition> BuildConditionAsync(Scope scope, PanelQuery query, CancellationToken cancellationToken)
    {
        BooleanCondition condition = scope.Condition;

        if (!string.IsNullOrWhiteSpace(query.Footprint))
        {
            condition = condition.With(FootprintPanel.Condition(query.Footprint));
        }

        if (query.FilterList.Count > 0)
        {
            var parsed = query.FilterList.Select(PanelFilter.Parse).ToList();
            PanelParameters parameters = await GetParametersAsync(scope, cancellationToken);

            foreach (PanelFilter filter in parsed)
            {
                condition = condition.With(parameters.BuildCondition(filter));
            }
        }

        foreach (string token in (query.Text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string pattern = "*" + token.Trim('*') + "*";
            condition = condition.With(
                BooleanCondition.Empty
                    .With(new WildcardCondition(SearchFieldNames.Core("Text"), pattern), SearchOccur.Should)
                    .With(new WildcardCondition(SearchFieldNames.Core("DynamicData"), pattern), SearchOccur.Should));
        }

        return condition;
    }

    private static BooleanCondition TypeCondition(BooleanCondition condition, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return condition;
        }

        BooleanCondition group = BooleanCondition.Empty;
        foreach (string path in paths)
        {
            group = group.With(new StrictCondition(SearchFieldNames.Parameter("ComponentType"), SearchFieldNames.ComponentTypeValue(path)), SearchOccur.Should);
        }

        return condition.With(group);
    }
}
