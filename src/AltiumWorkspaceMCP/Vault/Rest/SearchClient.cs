namespace AltiumWorkspaceMCP.Vault.Rest;

/// <summary>
/// The workspace search service (<c>REPORT /search/v1.0/searchasync</c>) — the same index
/// that the Components panel in Altium Designer uses.
/// </summary>
public sealed class SearchClient
{
    private const string SearchPath = "/v1.0/searchasync";

    private readonly ServiceDirectory _services;
    private readonly WorkspaceRestClient _rest;

    public SearchClient(ServiceDirectory services, WorkspaceRestClient rest)
    {
        _services = services;
        _rest = rest;
    }

    /// <summary>
    /// Conditions common to all component requests, as in the Components panel. <paramref name="excludedStates"/> —
    /// the lifecycle state GUIDs that the panel does not show (in the capture there are 13, all with the flag
    /// "not applicable": Obsolete, Abandoned, Deleted); without them the output includes all states.
    /// </summary>
    public static BooleanCondition ComponentsBase(IEnumerable<string>? excludedStates = null)
    {
        BooleanCondition condition = BooleanCondition.Empty
            .With(new StrictCondition(SearchFieldNames.Parameter("ContentType"), "Component"))
            .With(new WildcardCondition(SearchFieldNames.Core("Id"), "r_"))
            .With(new StrictCondition(SearchFieldNames.Parameter("LatestRevision"), "1"))
            .With(new StrictCondition(SearchFieldNames.Core("IsActive"), "0"), SearchOccur.MustNot);

        foreach (string state in excludedStates ?? [])
        {
            condition = condition.With(
                new StrictCondition(SearchFieldNames.Core("LifeCycleStateGUID"), state.ToLowerInvariant()),
                SearchOccur.MustNot);
        }

        return condition;
    }

    /// <summary>Runs a request; this is a read, a dry run does not block it.</summary>
    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        Uri baseUrl = await _services.GetAsync(ServiceDirectory.Kinds.SearchBase, cancellationToken);
        var url = new Uri(baseUrl.ToString().TrimEnd('/') + SearchPath);

        string text = await _rest.SendAsync(
            WorkspaceRestClient.Report, url, "searchasync", request.ToJson(), cancellationToken);

        return SearchResponse.Parse(text);
    }
}
