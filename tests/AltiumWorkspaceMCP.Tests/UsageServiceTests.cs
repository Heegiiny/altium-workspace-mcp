using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>Selection of "live" references from where-used links.</summary>
public sealed class UsageServiceTests
{
    private static ComponentRecord Record(string hrid, string revisionGuid, string folder = @"Components\X") => new()
    {
        ItemGuid = Guid.NewGuid().ToString(),
        Hrid = hrid,
        FolderPath = folder,
        FolderGuid = Guid.NewGuid().ToString(),
        RevisionGuid = revisionGuid,
    };

    [Fact]
    public void OnlyCurrentRevisionCounts()
    {
        var current = Record("CMP-001", "REV-CURRENT");
        var byRevision = new Dictionary<string, ComponentRecord>(StringComparer.OrdinalIgnoreCase)
        {
            ["REV-CURRENT"] = current,
        };

        // One reference is from the active revision, the other from an earlier one (it is no longer in the dictionary,
        // because UsageService builds the dictionary only from active revisions).
        var result = UsageService.SelectLiveUsage(["REV-CURRENT", "REV-OLD"], byRevision);

        Assert.Single(result);
        Assert.Equal("CMP-001", result[0].Hrid);
    }

    [Fact]
    public void NullRevisionGuidIsIgnored()
    {
        var byRevision = new Dictionary<string, ComponentRecord>(StringComparer.OrdinalIgnoreCase);

        var result = UsageService.SelectLiveUsage([null, string.Empty], byRevision);

        Assert.Empty(result);
    }

    [Fact]
    public void DuplicateRelationsToSameComponentAreCollapsed()
    {
        var current = Record("CMP-002", "REV-A");
        var byRevision = new Dictionary<string, ComponentRecord>(StringComparer.OrdinalIgnoreCase)
        {
            ["REV-A"] = current,
        };

        // One part can reference by several links (for example, a symbol and a footprint
        // by different roles of one revision where where-used returns several rows).
        var result = UsageService.SelectLiveUsage(["REV-A", "REV-A", "REV-A"], byRevision);

        Assert.Single(result);
    }

    [Fact]
    public void ResultIsOrderedByHrid()
    {
        var byRevision = new Dictionary<string, ComponentRecord>(StringComparer.OrdinalIgnoreCase)
        {
            ["REV-B"] = Record("CMP-010", "REV-B"),
            ["REV-A"] = Record("CMP-002", "REV-A"),
        };

        var result = UsageService.SelectLiveUsage(["REV-B", "REV-A"], byRevision);

        Assert.Equal(["CMP-002", "CMP-010"], result.Select(usage => usage.Hrid).ToArray());
    }
}
