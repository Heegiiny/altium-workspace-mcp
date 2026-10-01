using AltiumWorkspaceMCP.Soap.Vault;
using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>Protection against deleting what is in use, without blind spots.</summary>
public sealed class DeletionGuardTests
{
    private static WhereUsedResult Page(int count, int total) =>
        new(Enumerable.Range(0, count).Select(_ => new WhereUsedRelation { ParentRevisionGuid = Guid.NewGuid().ToString() }).ToList(), [], total);

    [Fact]
    public async Task PagingReadsRemainingPagesUntilTotal()
    {
        var starts = new List<int>();

        var result = await WhereUsedPaging.ReadAllAsync(
            (start, limit) =>
            {
                starts.Add(start);
                return Task.FromResult(Page(Math.Min(limit, 12 - start), 12));
            },
            pageSize: 5);

        Assert.Equal(12, result.Relations.Count);
        Assert.Equal([0, 5, 10], starts);
    }

    [Fact]
    public async Task PagingRefusesWhenServerStopsShortOfTotal()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WhereUsedPaging.ReadAllAsync(
                (start, _) => Task.FromResult(start == 0 ? Page(5, 9) : Page(0, 9)),
                pageSize: 5));

        Assert.Contains("could not fully check usage", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PagingSinglePageNeedsNoSecondRead()
    {
        int calls = 0;

        await WhereUsedPaging.ReadAllAsync(
            (_, _) => { calls++; return Task.FromResult(Page(3, 3)); },
            pageSize: 5);

        Assert.Equal(1, calls);
    }

    private static IReadOnlyList<TemplateModelReference> Index() =>
        TemplateReferenceIndex.Build(
        [
            ("CMPT-0007", "tpl-7", new TemplateSettings(null, null, false, "sym-1", "fp-0402")),
            ("CMPT-0008", "tpl-8", new TemplateSettings(null, null, false, null, "fp-0402")),
        ]);

    [Fact]
    public void ModelIsBlockedAsDefaultFootprintOfTemplates()
    {
        var found = TemplateReferenceIndex.Find(Index(), "FP-0402", new HashSet<string>());

        Assert.Equal(2, found.Count);
        Assert.Contains(found, text => text.StartsWith("CMPT-0007") && text.Contains("footprint"));
    }

    [Fact]
    public void ModelIsBlockedAsDefaultSymbolOfTemplate()
    {
        var found = TemplateReferenceIndex.Find(Index(), "sym-1", new HashSet<string>());

        Assert.Single(found);
        Assert.Contains("symbol", found[0]);
    }

    [Fact]
    public void TemplateDeletedTogetherWithModelDoesNotBlock()
    {
        var found = TemplateReferenceIndex.Find(Index(), "fp-0402", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tpl-7" });

        Assert.Single(found);
        Assert.StartsWith("CMPT-0008", found[0]);
    }

    [Fact]
    public void UnreferencedModelHasNoTemplates() =>
        Assert.Empty(TemplateReferenceIndex.Find(Index(), "other", new HashSet<string>()));

    [Fact]
    public void DetailsNameTemplatesAndLiveParts()
    {
        var text = DeletionGuard.DescribeDetails(
        [
            new BlockedModel("g", "0402-000000", [new LiveUsage("CMP-000-00212", @"Components\T")], ["CMPT-0007 (footprint as default)"]),
        ]);

        Assert.Contains("CMP-000-00212", text);
        Assert.Contains("CMPT-0007", text);
    }
}
