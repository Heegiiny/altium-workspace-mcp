using System.Text.Json;
using System.Text.Json.Nodes;

namespace AltiumWorkspaceMCP.Vault.Rest;

/// <summary>How a condition enters a boolean query (the <c>Occur</c> field): checked against Altium Designer traffic.</summary>
public enum SearchOccur
{
    /// <summary>The condition is required (AND).</summary>
    Must = 0,

    /// <summary>Any one of the conditions of this group is enough (OR).</summary>
    Should = 1,

    /// <summary>The condition excludes the document (NOT).</summary>
    MustNot = 2,
}

/// <summary>A search condition: a strict match, a pattern or a boolean combination.</summary>
public abstract record SearchCondition
{
    /// <summary>The JSON node of the condition in the form the search service accepts.</summary>
    public abstract JsonObject ToJson();
}

/// <summary>A strict field value match (<c>DtoSearchConditionStrictQuery</c>).</summary>
public sealed record StrictCondition(string Field, string Value) : SearchCondition
{
    public override JsonObject ToJson() => new()
    {
        ["$type"] = "DtoSearchConditionStrictQuery",
        ["Term"] = SearchTerm.ToJson(Field, Value),
    };
}

/// <summary>A pattern match: <c>*</c> and <c>?</c> in the value (<c>DtoSearchConditionWildcardQuery</c>).</summary>
public sealed record WildcardCondition(string Field, string Value) : SearchCondition
{
    public override JsonObject ToJson() => new()
    {
        ["$type"] = "DtoSearchConditionWildcardQuery",
        ["Term"] = SearchTerm.ToJson(Field, Value),
    };
}

/// <summary>
/// A numeric field range (<c>DtoSearchConditionRangeQuery</c>). The bounds are given as numbers in
/// plain notation (<c>1E-07</c> or <c>0.0000001</c>); a missing bound — no limit.
/// Checked on the server: without <c>MinInclusive</c>/<c>MaxInclusive</c> the bounds are excluded.
/// </summary>
public sealed record RangeCondition(string Field, string? Min, string? Max, bool MinInclusive = true, bool MaxInclusive = true)
    : SearchCondition
{
    public override JsonObject ToJson()
    {
        var node = new JsonObject
        {
            ["$type"] = "DtoSearchConditionRangeQuery",
            ["Field"] = Field,
        };

        if (Min is not null)
        {
            node["Min"] = Min;
        }

        if (Max is not null)
        {
            node["Max"] = Max;
        }

        node["MinInclusive"] = MinInclusive;
        node["MaxInclusive"] = MaxInclusive;
        return node;
    }
}

/// <summary>A boolean query element: a condition and the way it is applied.</summary>
public sealed record BooleanItem(SearchCondition Condition, SearchOccur Occur);

/// <summary>A boolean query — a list of conditions with <see cref="SearchOccur"/> (<c>DtoSearchConditionBooleanQuery</c>).</summary>
public sealed record BooleanCondition(IReadOnlyList<BooleanItem> Items) : SearchCondition
{
    public static BooleanCondition Empty { get; } = new([]);

    /// <summary>A copy of the query with an added condition.</summary>
    public BooleanCondition With(SearchCondition condition, SearchOccur occur = SearchOccur.Must) =>
        new([.. Items, new BooleanItem(condition, occur)]);

    public override JsonObject ToJson()
    {
        var items = new JsonArray();

        foreach (BooleanItem item in Items)
        {
            items.Add(new JsonObject
            {
                ["$type"] = "DtoSearchConditionBooleanQueryItem",
                ["Item"] = item.Condition.ToJson(),
                ["Occur"] = (int)item.Occur,
            });
        }

        return new JsonObject
        {
            ["$type"] = "DtoSearchConditionBooleanQuery",
            ["Items"] = items,
        };
    }
}

internal static class SearchTerm
{
    public static JsonObject ToJson(string field, string value) => new()
    {
        ["$type"] = "DtoSearchConditionTerm",
        ["Field"] = field,
        ["Value"] = value,
    };
}

/// <summary>Result sorting: the field name ("&lt;score&gt;" — by relevance) and the direction.</summary>
public sealed record SearchSort(string Field, bool Descending);

/// <summary>A search service request (<c>SearchRequest</c>).</summary>
public sealed record SearchRequest
{
    public required BooleanCondition Condition { get; init; }

    /// <summary>Sorting; empty — by relevance.</summary>
    public IReadOnlyList<SearchSort> Sort { get; init; } = [];

    /// <summary>Which fields to return; empty — all document fields.</summary>
    public IReadOnlyList<string> ReturnFields { get; init; } = [];

    public int Start { get; init; }

    /// <summary>How many documents to return; 0 — only the total count and facets.</summary>
    public int Limit { get; init; } = 50;

    /// <summary>Return facets: field values with document counts.</summary>
    public bool IncludeFacets { get; init; }

    /// <summary>The call body: <c>{"request": …}</c>.</summary>
    public string ToJson()
    {
        var sort = new JsonArray();
        foreach (SearchSort item in Sort)
        {
            sort.Add(new JsonObject
            {
                ["$type"] = "DtoSortSearchField",
                ["Name"] = item.Field,
                ["Order"] = item.Descending ? 1 : 0,
            });
        }

        var returnFields = new JsonArray();
        foreach (string field in ReturnFields)
        {
            returnFields.Add(field);
        }

        var request = new JsonObject
        {
            ["$type"] = "SearchRequest",
            ["Condition"] = Condition.ToJson(),
            ["SortFields"] = sort,
            ["ReturnFields"] = returnFields,
            ["Start"] = Start,
            ["Limit"] = Limit,
            ["IncludeFacets"] = IncludeFacets,
            ["UseOnlyBestFacets"] = IncludeFacets,
            ["IncludeDebugInfo"] = false,
            ["IgnoreCaseFieldNames"] = false,
            ["TrackTotalHits"] = true,
        };

        return new JsonObject { ["request"] = request }.ToJsonString();
    }
}

/// <summary>A found document: fields by name (values are strings, as the service returns them).</summary>
public sealed record SearchDocument(double Score, IReadOnlyDictionary<string, string> Fields)
{
    /// <summary>The value of a field or <c>null</c> if the document does not contain it.</summary>
    public string? Get(string field) => Fields.TryGetValue(field, out string? value) ? value : null;
}

/// <summary>A single facet value and the document count with it.</summary>
public sealed record FacetCounter(string Value, int Count);

/// <summary>A facet: field values with document counts; for numeric fields — the range bounds.</summary>
public sealed record SearchFacet(
    string Name,
    int TotalHitCount,
    IReadOnlyList<FacetCounter> Counters,
    bool SupportsRange,
    string? MinValue,
    string? MaxValue);

/// <summary>The search service response.</summary>
public sealed record SearchResponse(
    long Total,
    IReadOnlyList<SearchDocument> Documents,
    IReadOnlyList<SearchFacet> Facets)
{
    /// <summary>Parses the body of the <c>searchasync</c> response (pure logic).</summary>
    public static SearchResponse Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        if (root.TryGetProperty("Success", out JsonElement success) && success.ValueKind == JsonValueKind.False)
        {
            string message = root.TryGetProperty("ErrorMessage", out JsonElement error) ? error.ToString() : json;
            throw new InvalidOperationException(
                $"The search service rejected the request: {(message.Length > 400 ? message[..400] + " …" : message)}");
        }

        long total = root.TryGetProperty("Total", out JsonElement totalElement) ? totalElement.GetInt64() : 0;
        var documents = new List<SearchDocument>();
        var facets = new List<SearchFacet>();

        if (root.TryGetProperty("Documents", out JsonElement documentsElement) && documentsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in documentsElement.EnumerateArray())
            {
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);

                if (item.TryGetProperty("Fields", out JsonElement fieldsElement) && fieldsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement field in fieldsElement.EnumerateArray())
                    {
                        string? name = field.GetProperty("Name").GetString();
                        if (name is not null)
                        {
                            fields[name] = field.TryGetProperty("Value", out JsonElement value) ? value.ToString() : string.Empty;
                        }
                    }
                }

                double score = item.TryGetProperty("Score", out JsonElement scoreElement) && scoreElement.ValueKind == JsonValueKind.Number
                    ? scoreElement.GetDouble()
                    : 0;
                documents.Add(new SearchDocument(score, fields));
            }
        }

        if (root.TryGetProperty("FacetedCounters", out JsonElement facetsElement) && facetsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in facetsElement.EnumerateArray())
            {
                var counters = new List<FacetCounter>();

                if (item.TryGetProperty("Counters", out JsonElement countersElement) && countersElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement counter in countersElement.EnumerateArray())
                    {
                        counters.Add(new FacetCounter(
                            counter.GetProperty("Value").ToString(),
                            counter.GetProperty("Count").GetInt32()));
                    }
                }

                facets.Add(new SearchFacet(
                    item.GetProperty("FacetName").GetString() ?? string.Empty,
                    item.TryGetProperty("TotalHitCount", out JsonElement hits) ? hits.GetInt32() : counters.Sum(counter => counter.Count),
                    counters,
                    item.TryGetProperty("SupportRange", out JsonElement range) && range.ValueKind == JsonValueKind.True,
                    item.TryGetProperty("MinValue", out JsonElement min) ? min.ToString() : null,
                    item.TryGetProperty("MaxValue", out JsonElement max) ? max.ToString() : null));
            }
        }

        return new SearchResponse(total, documents, facets);
    }
}
