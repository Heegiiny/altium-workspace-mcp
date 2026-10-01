using System.Globalization;
using AltiumWorkspaceMCP.Vault.Rest;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>How a parameter value is compared in a panel filter.</summary>
public enum FilterOperator
{
    /// <summary><c>Name=Value</c>.</summary>
    Equal,

    /// <summary><c>Name&gt;=Value</c> (numeric parameters only).</summary>
    AtLeast,

    /// <summary><c>Name&lt;=Value</c> (numeric parameters only).</summary>
    AtMost,
}

/// <summary>A Components panel filter: <c>Value=10k</c>, <c>Voltage Rating&gt;=16</c>.</summary>
public sealed record PanelFilter(string Name, FilterOperator Operator, string Value)
{
    public const string Format = "'Name=Value', 'Name>=Value' or 'Name<=Value', for example Value=10k, Voltage Rating>=16";

    /// <summary>Parses a filter string; a refusal hints at the format.</summary>
    public static PanelFilter Parse(string text)
    {
        int atLeast = text.IndexOf(">=", StringComparison.Ordinal);
        int atMost = text.IndexOf("<=", StringComparison.Ordinal);
        int equal = text.IndexOf('=');

        (int index, int length, FilterOperator op) = (atLeast, atMost, equal) switch
        {
            ( > 0, _, _) when atMost < 0 || atLeast < atMost => (atLeast, 2, FilterOperator.AtLeast),
            (_, > 0, _) => (atMost, 2, FilterOperator.AtMost),
            (_, _, > 0) => (equal, 1, FilterOperator.Equal),
            _ => (-1, 0, FilterOperator.Equal),
        };

        string name = index > 0 ? text[..index].Trim() : string.Empty;
        string value = index > 0 ? text[(index + length)..].Trim() : string.Empty;

        if (name.Length == 0 || value.Length == 0)
        {
            throw new ArgumentException($"Filter '{text}' could not be parsed. Format: {Format}.");
        }

        return new PanelFilter(name, op, value);
    }
}

/// <summary>A component type parameter by the facet data: which fields represent it in the index.</summary>
public sealed record PanelParameter(
    string Name,
    string? TextField,
    string? NumericField,
    string? TypeGuid,
    SearchFacet? TextFacet,
    SearchFacet? NumericFacet)
{
    /// <summary>How many parts of the selection contain the parameter.</summary>
    public int Filled => Math.Max(TextFacet?.TotalHitCount ?? 0, NumericFacet?.TotalHitCount ?? 0);

    /// <summary>The parameter type is numeric: the value has a number in base units.</summary>
    public bool IsNumeric => NumericField is not null;
}

/// <summary>Parameters found in the facets of a search response, and parsing of filters against them.</summary>
public sealed class PanelParameters
{
    /// <summary>
    /// Panel fields that are not part parameters: their values are already in the table
    /// columns or tell the agent nothing.
    /// </summary>
    private static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ComponentType", "CreatedBy", "ModifiedBy", "LifeCycle", "Cat", "ContentType", "LatestRevision",
        "NamingSchemeGuid", "ItemHRID", "ReleaseDate", "ReleaseDateNum",
    };

    private readonly Dictionary<string, PanelParameter> _byName;

    private PanelParameters(IReadOnlyList<PanelParameter> all)
    {
        All = all;
        _byName = all.ToDictionary(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Selection parameters, the most filled first.</summary>
    public IReadOnlyList<PanelParameter> All { get; }

    /// <summary>A panel field is not a part parameter (type, service dates, footprint, links).</summary>
    public static bool IsSystemName(string name) =>
        SystemNames.Contains(name)
        || name.StartsWith("Footprint", StringComparison.OrdinalIgnoreCase)
        || (name.StartsWith("ComponentLink", StringComparison.OrdinalIgnoreCase) && name.Length > "ComponentLink".Length
            && char.IsAsciiDigit(name["ComponentLink".Length]));

    /// <summary>Builds parameters from the response facets: a numeric field and its text "mirror" are one parameter.</summary>
    public static PanelParameters FromFacets(IEnumerable<SearchFacet> facets)
    {
        var groups = new Dictionary<string, (SearchFacet? Text, string? TextField, SearchFacet? Numeric, string? NumericField, string? Guid)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (SearchFacet facet in facets)
        {
            ParsedFieldName? parsed = SearchFieldNames.Parse(facet.Name);

            if (parsed is not { Group: FieldGroup.Parameters } || IsSystemName(parsed.Parameter))
            {
                continue;
            }

            groups.TryGetValue(parsed.Parameter, out var current);
            groups[parsed.Parameter] = parsed.Kind == FieldKind.Numeric
                ? (current.Text, current.TextField, facet, facet.Name, parsed.TypeGuid)
                : (facet, facet.Name, current.Numeric, current.NumericField, current.Guid);
        }

        var parameters = groups
            .Select(pair => new PanelParameter(
                pair.Key, pair.Value.TextField, pair.Value.NumericField, pair.Value.Guid, pair.Value.Text, pair.Value.Numeric))
            .OrderByDescending(parameter => parameter.Filled)
            .ThenBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PanelParameters(parameters);
    }

    /// <summary>A parameter by name, case-insensitive; a refusal hints at the nearest names.</summary>
    public PanelParameter Find(string name)
    {
        if (_byName.TryGetValue(name.Trim(), out PanelParameter? parameter))
        {
            return parameter;
        }

        IReadOnlyList<string> near = NameSuggester.Nearest(name, All.Select(item => item.Name));
        string hint = near.Count > 0
            ? $" Similar: {string.Join(", ", near)}."
            : $" The selection has {All.Count} parameters, the most filled: {string.Join(", ", All.Take(8).Select(item => item.Name))}.";

        throw new InvalidOperationException(
            $"The selection's parts have no parameter '{name}'.{hint} The full list of values and parameters — vault_components action=facets.");
    }

    /// <summary>A search condition for a filter. Numbers are compared by the numeric field, text — by the text field.</summary>
    public SearchCondition BuildCondition(PanelFilter filter)
    {
        PanelParameter parameter = Find(filter.Name);
        bool numeric = TryNumber(parameter, filter.Value, out double number);

        if (filter.Operator == FilterOperator.Equal)
        {
            if (numeric)
            {
                return new StrictCondition(parameter.NumericField!, FormatNumber(number));
            }

            string? textField = parameter.TextField;
            if (textField is null)
            {
                throw new ArgumentException(
                    $"Value '{filter.Value}' of parameter '{parameter.Name}' is not a number: {NumberHint(parameter)}");
            }

            return filter.Value.Contains('*') || filter.Value.Contains('?')
                ? new WildcardCondition(textField, filter.Value)
                : new StrictCondition(textField, filter.Value);
        }

        if (!parameter.IsNumeric)
        {
            throw new ArgumentException(
                $"Parameter '{parameter.Name}' is text: the comparison '>=' and '<=' is impossible for it. Use '{parameter.Name}=value' "
                + "(* and ? are allowed in the value).");
        }

        if (!numeric)
        {
            throw new ArgumentException($"Value '{filter.Value}' of parameter '{parameter.Name}' is not a number: {NumberHint(parameter)}");
        }

        string formatted = FormatNumber(number);
        return filter.Operator == FilterOperator.AtLeast
            ? new RangeCondition(parameter.NumericField!, formatted, null, MinInclusive: true)
            : new RangeCondition(parameter.NumericField!, null, formatted, MaxInclusive: true);
    }

    private static bool TryNumber(PanelParameter parameter, string value, out double number)
    {
        number = 0;

        if (!parameter.IsNumeric)
        {
            return false;
        }

        if (ParameterValueCodec.TryParse(value, parameter.TypeGuid, out number))
        {
            return true;
        }

        // The parameter type is unknown to the codec — a number in base units without prefixes is accepted.
        return !ParameterValueCodec.IsKnown(parameter.TypeGuid)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    private static string NumberHint(PanelParameter parameter)
    {
        string? unit = ParameterValueCodec.UnitOf(parameter.TypeGuid);
        string type = ParameterValueCodec.DescribeType(parameter.TypeGuid);
        return string.IsNullOrEmpty(unit)
            ? $"the parameter type is {type}, write a number, for example 10 or 4.7."
            : $"the parameter type is {type}, write a number with a prefix and the unit {unit}, for example 10k{unit} or 4.7k.";
    }

    /// <summary>A number for a search condition: invariant notation without loss of precision.</summary>
    public static string FormatNumber(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>A type tree row for vault_components: the path, the part count and the hidden nested types.</summary>
public sealed record TypeLine(string Path, int Own, int Total, int Hidden);

/// <summary>The component type tree with part counts by the <c>ComponentType</c> facet data.</summary>
public static class ComponentTypeTree
{
    /// <summary>The type path in the comparison key: lower case, without a trailing slash.</summary>
    public static string Key(string path) => path.Trim().Trim('\\').ToLowerInvariant();

    /// <summary>
    /// Builds the tree rows. <paramref name="depth"/> — how many levels to show: without <paramref name="under"/> — from
    /// the top level, with it — the type itself and <paramref name="depth"/> levels under it. Types without parts are not
    /// shown, but are counted in <c>Hidden</c> only if they are deeper than the shown level.
    /// </summary>
    /// <param name="nodes">Types from the vault tags (paths with the original case).</param>
    /// <param name="counts">Part count by type: the key is <see cref="Key"/>.</param>
    public static IReadOnlyList<TypeLine> Build(
        IReadOnlyList<ComponentTypeNode> nodes,
        IReadOnlyDictionary<string, int> counts,
        string? under,
        int depth)
    {
        // All known paths: from tags (with the real case) and from the facet (a type may have been deleted).
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ComponentTypeNode node in nodes)
        {
            names[Key(node.Path)] = node.Path;
        }

        foreach (string key in counts.Keys)
        {
            names.TryAdd(Key(key), key.Trim('\\'));
        }

        string root = under is null ? string.Empty : Key(under);
        int rootLevel = root.Length == 0 ? 0 : root.Split('\\').Length;

        var lines = new List<TypeLine>();

        foreach ((string key, string path) in names.OrderBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase))
        {
            if (root.Length > 0 && key != root && !key.StartsWith(root + "\\", StringComparison.Ordinal))
            {
                continue;
            }

            int level = key.Split('\\').Length - rootLevel;
            int maxLevel = depth;

            if (level > maxLevel || (root.Length == 0 && level < 1))
            {
                continue;
            }

            int own = counts.TryGetValue(key, out int ownCount) ? ownCount : 0;
            int total = 0;
            int hidden = 0;

            foreach ((string otherKey, _) in names)
            {
                if (otherKey != key && !otherKey.StartsWith(key + "\\", StringComparison.Ordinal))
                {
                    continue;
                }

                int count = counts.TryGetValue(otherKey, out int otherCount) ? otherCount : 0;
                total += count;

                // Only nested types with parts are considered hidden.
                int otherLevel = otherKey.Split('\\').Length - rootLevel;
                if (otherKey != key && otherLevel > maxLevel && count > 0)
                {
                    hidden++;
                }
            }

            if (total > 0)
            {
                lines.Add(new TypeLine(path, own, total, hidden));
            }
        }

        return lines;
    }

    /// <summary>Paths of the type and all nested ones — the parts of the type with its subtypes are selected by them.</summary>
    public static IReadOnlyList<string> WithDescendants(IReadOnlyList<ComponentTypeNode> nodes, ComponentTypeNode type) =>
        nodes
            .Where(node => node.Guid == type.Guid
                || Key(node.Path).StartsWith(Key(type.Path) + "\\", StringComparison.Ordinal))
            .Select(node => node.Path)
            .ToList();

    /// <summary>
    /// Finds a type by GUID, full path, path ending (<c>Resistors Small</c>), name or part of a name.
    /// Reading forgives inexactness, but the result is named in the response; ambiguity and a typo — a refusal with a hint.
    /// </summary>
    public static ComponentTypeNode Resolve(IReadOnlyList<ComponentTypeNode> nodes, string input)
    {
        string wanted = input.Replace('/', '\\').Trim().Trim('\\');
        string key = Key(wanted);

        ComponentTypeNode? byGuid = nodes.FirstOrDefault(node => string.Equals(node.Guid, wanted, StringComparison.OrdinalIgnoreCase));
        if (byGuid is not null)
        {
            return byGuid;
        }

        foreach (Func<ComponentTypeNode, bool> match in new Func<ComponentTypeNode, bool>[]
        {
            node => Key(node.Path) == key,
            node => Key(node.Path).EndsWith("\\" + key, StringComparison.Ordinal),
            node => Key(node.Name) == key,
            node => Key(node.Path).Contains(key, StringComparison.Ordinal),
        })
        {
            var found = nodes.Where(match).ToList();

            if (found.Count == 1)
            {
                return found[0];
            }

            if (found.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Type '{input}' matches several types: {string.Join("; ", found.Take(10).Select(node => node.Path))}. "
                    + "Specify the full path of one of them.");
            }
        }

        IReadOnlyList<string> near = NameSuggester.Nearest(wanted, nodes.Select(node => node.Path).Concat(nodes.Select(node => node.Name)));
        throw new InvalidOperationException(
            $"Component type '{input}' not found."
            + (near.Count > 0 ? $" Similar: {string.Join("; ", near)}." : string.Empty)
            + " The type tree with part counts — vault_components action=types.");
    }
}
