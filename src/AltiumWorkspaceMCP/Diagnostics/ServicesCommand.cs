using AltiumWorkspaceMCP.Configuration;
using AltiumWorkspaceMCP.Vault;
using AltiumWorkspaceMCP.Vault.Rest;

namespace AltiumWorkspaceMCP.Diagnostics;

/// <summary>
/// Checking the workspace REST services: the service addresses and a request to the search service
/// "all components" — the total count and the largest component types.
/// </summary>
public static class ServicesCommand
{
    private const int TopTypes = 10;

    public static async Task<int> RunAsync(VaultOptions options)
    {
        await using var workspace = new VaultWorkspace(options);

        IReadOnlyDictionary<string, Uri> services =
            await workspace.ServiceDirectory.GetAllAsync(CancellationToken.None);
        Console.Error.WriteLine($"Services in the directory: {services.Count}. Needed by the server:");
        foreach (string kind in new[] { ServiceDirectory.Kinds.SearchBase, ServiceDirectory.Kinds.PartCatalogApi, "VAULT", "IDS" })
        {
            Console.Error.WriteLine($"  {kind,-16} {(services.TryGetValue(kind, out Uri? url) ? url : "— none —")}");
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        SearchResponse response = await workspace.Search.SearchAsync(
            new SearchRequest
            {
                Condition = SearchClient.ComponentsBase(),
                Limit = 0,
                IncludeFacets = true,
            },
            CancellationToken.None);
        watch.Stop();

        Console.Error.WriteLine();
        Console.Error.WriteLine($"Search 'all components': Total = {response.Total}, facets {response.Facets.Count}, {watch.ElapsedMilliseconds} ms");

        string typeFacet = SearchFieldNames.Parameter("ComponentType");
        SearchFacet? types = response.Facets.FirstOrDefault(facet => facet.Name == typeFacet);
        if (types is null)
        {
            Console.Error.WriteLine($"No type facet ({typeFacet}) in the response. Available: "
                + string.Join(", ", response.Facets.Select(facet => facet.Name).Take(15)));
            return 1;
        }

        Console.Error.WriteLine($"Component types (top {TopTypes} of {types.Counters.Count}, total with a type {types.TotalHitCount}):");
        foreach (FacetCounter counter in types.Counters.OrderByDescending(item => item.Count).Take(TopTypes))
        {
            Console.Error.WriteLine($"  {counter.Count,6}  {counter.Value}");
        }

        return 0;
    }

    /// <summary>Sends the search service a request body from a file as is and prints the response (for format research).</summary>
    public static async Task<int> RawSearchAsync(VaultOptions options, string file)
    {
        await using var workspace = new VaultWorkspace(options);

        Uri baseUrl = await workspace.ServiceDirectory.GetAsync(ServiceDirectory.Kinds.SearchBase, CancellationToken.None);
        var url = new Uri(baseUrl.ToString().TrimEnd('/') + "/v1.0/searchasync");

        try
        {
            string body = await File.ReadAllTextAsync(file);
            string text = await workspace.Rest.SendAsync(
                WorkspaceRestClient.Report, url, "searchasync", body, CancellationToken.None);
            Console.WriteLine(text);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
