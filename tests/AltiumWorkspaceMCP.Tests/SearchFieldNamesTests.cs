using System.Text.Json;
using AltiumWorkspaceMCP.Vault.Rest;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>Search service field names and request bodies: the examples are from Altium Designer traffic.</summary>
public sealed class SearchFieldNamesTests
{
    [Theory]
    [InlineData("Case/Package", "Case_2FPackage")]
    [InlineData("RoHS Compliant", "RoHS_20Compliant")]
    [InlineData("LCSC Part#", "LCSC_20Part_23")]
    [InlineData("Value", "Value")]
    [InlineData("Value_T@x^", "Value_5FT_40x_5E")]
    [InlineData("Voltage-Rating (V)", "Voltage_2DRating_20_28V_29")]
    public void EscapeReplacesEverythingExceptLatinLettersAndDigits(string name, string expected) =>
        Assert.Equal(expected, SearchFieldNames.Escape(name));

    [Fact]
    public void ParameterFieldGetsParametersGroupSuffix() =>
        Assert.Equal("Case_2FPackageDD420E8DDD8B445E911A0601BB2B6D53", SearchFieldNames.Parameter("Case/Package"));

    [Fact]
    public void CoreFieldGetsRevisionCoreSuffix() =>
        Assert.Equal("HRIDC623975962814A5FAAD7FA1CD85DA0DB", SearchFieldNames.Core("HRID"));

    [Fact]
    public void NumericParameterFieldIsBuiltFromNameAndTypeGuid() =>
        Assert.Equal(
            "Value_5FB90F0DAE_2DB695_2D41F5_2DBCB0_2D0DE5F75C9E50DD420E8DDD8B445E911A0601BB2B6D53",
            SearchFieldNames.NumericParameter("Value", "B90F0DAE-B695-41F5-BCB0-0DE5F75C9E50"));

    [Theory]
    [InlineData("Passive\\Resistors", "Passive\\Resistors\\")]
    [InlineData("Passive\\Resistors\\", "Passive\\Resistors\\")]
    public void ComponentTypeValueEndsWithBackslash(string path, string expected) =>
        Assert.Equal(expected, SearchFieldNames.ComponentTypeValue(path));

    [Fact]
    public void RequestBodyMatchesCapturedShape()
    {
        BooleanCondition condition = SearchClient.ComponentsBase()
            .With(
                BooleanCondition.Empty.With(
                    new StrictCondition(SearchFieldNames.Parameter("ComponentType"), "Passive\\Resistors\\Resistors Small\\"),
                    SearchOccur.Should));

        string body = new SearchRequest { Condition = condition, Limit = 0, IncludeFacets = true }.ToJson();

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement request = document.RootElement.GetProperty("request");

        Assert.Equal("SearchRequest", request.GetProperty("$type").GetString());
        Assert.Equal(0, request.GetProperty("Limit").GetInt32());
        Assert.True(request.GetProperty("IncludeFacets").GetBoolean());

        JsonElement[] items = request.GetProperty("Condition").GetProperty("Items").EnumerateArray().ToArray();
        Assert.Equal(5, items.Length);

        JsonElement contentType = items[0];
        Assert.Equal("DtoSearchConditionBooleanQueryItem", contentType.GetProperty("$type").GetString());
        Assert.Equal(0, contentType.GetProperty("Occur").GetInt32());
        Assert.Equal("DtoSearchConditionStrictQuery", contentType.GetProperty("Item").GetProperty("$type").GetString());
        JsonElement term = contentType.GetProperty("Item").GetProperty("Term");
        Assert.Equal("DtoSearchConditionTerm", term.GetProperty("$type").GetString());
        Assert.Equal("ContentTypeDD420E8DDD8B445E911A0601BB2B6D53", term.GetProperty("Field").GetString());
        Assert.Equal("Component", term.GetProperty("Value").GetString());

        Assert.Equal("DtoSearchConditionWildcardQuery", items[1].GetProperty("Item").GetProperty("$type").GetString());
        Assert.Equal(2, items[3].GetProperty("Occur").GetInt32());

        JsonElement nested = items[4].GetProperty("Item");
        Assert.Equal("DtoSearchConditionBooleanQuery", nested.GetProperty("$type").GetString());
        Assert.Equal(1, nested.GetProperty("Items")[0].GetProperty("Occur").GetInt32());
    }

    [Fact]
    public void ResponseParsesDocumentsAndFacets()
    {
        const string json = """
            {
              "Total": 2266,
              "Success": true,
              "Documents": [
                { "Score": 2.4, "Fields": [
                    { "Name": "HRIDC623975962814A5FAAD7FA1CD85DA0DB", "Value": "CMP-000-00125-4", "FieldType": 3 },
                    { "Name": "Value_5FB90F0DAE_2DB695_2D41F5_2DBCB0_2D0DE5F75C9E50DD420E8DDD8B445E911A0601BB2B6D53", "Value": "51000", "FieldType": 1 } ] }
              ],
              "FacetedCounters": [
                { "FacetName": "Case_2FPackageDD420E8DDD8B445E911A0601BB2B6D53", "TotalHitCount": 2263,
                  "Counters": [ { "Value": "0402", "Count": 1077 }, { "Value": "0603", "Count": 885 } ], "SupportRange": false },
                { "FacetName": "Value_5FB90F0DAE_2DB695_2D41F5_2DBCB0_2D0DE5F75C9E50DD420E8DDD8B445E911A0601BB2B6D53",
                  "TotalHitCount": 2266, "Counters": [ { "Value": "1000", "Count": 13 } ],
                  "SupportRange": true, "MinValue": "0", "MaxValue": "10000000" }
              ]
            }
            """;

        SearchResponse response = SearchResponse.Parse(json);

        Assert.Equal(2266, response.Total);
        SearchDocument document = Assert.Single(response.Documents);
        Assert.Equal("CMP-000-00125-4", document.Get(SearchFieldNames.Core("HRID")));
        Assert.Null(document.Get("no such"));

        Assert.Equal(2, response.Facets.Count);
        Assert.Equal([1077, 885], response.Facets[0].Counters.Select(counter => counter.Count));
        Assert.False(response.Facets[0].SupportsRange);
        Assert.True(response.Facets[1].SupportsRange);
        Assert.Equal("10000000", response.Facets[1].MaxValue);
    }

    [Fact]
    public void ResponseWithFailureThrowsReadableError()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => SearchResponse.Parse("""{"Success": false, "ErrorMessage": "Unknown field"}"""));

        Assert.Contains("Unknown field", error.Message);
    }

    [Fact]
    public void ServiceDirectoryParsesEndpoints()
    {
        const string xml = """
            <Envelope><Body><GetServicesEndPointsResponse><GetServicesEndPointsResult>
              <EndPointInfo><ServiceKind>SEARCHBASE</ServiceKind><ServiceUrl>http://host:9780/search</ServiceUrl></EndPointInfo>
              <EndPointInfo><ServiceKind>PARTCATALOG_API</ServiceKind><ServiceUrl>http://host:9780/catalog2/api/</ServiceUrl></EndPointInfo>
              <EndPointInfo><ServiceKind>BROKEN</ServiceKind><ServiceUrl>not a url</ServiceUrl></EndPointInfo>
            </GetServicesEndPointsResult></GetServicesEndPointsResponse></Body></Envelope>
            """;

        IReadOnlyDictionary<string, Uri> services = ServiceDirectory.Parse(xml);

        Assert.Equal(2, services.Count);
        Assert.Equal("http://host:9780/search", services[ServiceDirectory.Kinds.SearchBase].ToString());
        Assert.Equal("http://host:9780/catalog2/api/", services["partcatalog_api"].ToString());
    }
}
