using System.Text.Json;
using AltiumWorkspaceMCP.Tools;
using AltiumWorkspaceMCP.Vault;
using ModelContextProtocol;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>
/// Trash list response: items get the budget first, folders get the remainder;
/// a page with items shows at least one and itemsNextOffset grows — the agent does not loop.
/// </summary>
public sealed class TrashResponseTests
{
    private const int MaxChars = 20_000;

    private static readonly DateTimeOffset Deleted = new(2026, 9, 22, 19, 50, 0, TimeSpan.Zero);

    private static string LongPath(string prefix, int number) =>
        $@"Components\{prefix}\A very long path to check the response budget\Another long folder segment\Folder number {number:D4}\"
        // Padding so that the paths stay as expensive as the original long Russian ones.
        + new string('x', 500);

    private static List<TrashFolderEntry> Folders(int count) =>
        Enumerable.Range(0, count)
            .Select(number => new TrashFolderEntry($"F{number:D6}-0000-0000-0000-000000000000", LongPath("Folders", number), Deleted))
            .ToList();

    private static List<TrashItemEntry> Items(int count) =>
        Enumerable.Range(0, count)
            .Select(number => new TrashItemEntry(
                $"I{number:D6}-0000-0000-0000-000000000000",
                $"PCC-{number:D6}",
                "altium-pcb-component",
                LongPath("Items", number),
                Deleted,
                LongPath("Deleted", number),
                new UsageSummary(3, ["CMP-0001", "CMP-0002", "CMP-0003"]),
                new UsageSummary(0, [])))
            .ToList();

    /// <summary>A page as TrashService.ListAsync returns it: slices by offset/limit and continuation offsets.</summary>
    private static TrashListing Listing(
        IReadOnlyList<TrashFolderEntry> folders,
        IReadOnlyList<TrashItemEntry> items,
        int offset,
        int foldersOffset,
        int limit)
    {
        var folderPage = folders.Skip(foldersOffset).Take(limit).ToList();
        var itemPage = items.Skip(offset).Take(limit).ToList();

        return new TrashListing(
            folderPage,
            folders.Count,
            foldersOffset,
            foldersOffset + folderPage.Count < folders.Count ? foldersOffset + folderPage.Count : null,
            itemPage,
            items.Count,
            offset,
            offset + itemPage.Count < items.Count ? offset + itemPage.Count : null,
            TimedOut: false);
    }

    private sealed record Shown(
        List<string> ItemGuids,
        List<string> FolderGuids,
        int? ItemsNextOffset,
        int? FoldersNextOffset,
        int Size);

    private static Shown Render(TrashListing listing)
    {
        object response = TrashResponse.Build(listing, MaxChars);
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(response, McpJsonUtilities.DefaultOptions));
        JsonElement root = document.RootElement;

        // The MCP serializer does not write empty (null) fields: no field — no continuation.
        int? Next(string name) =>
            root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

        return new Shown(
            root.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("guid").GetString()!).ToList(),
            root.GetProperty("folders").EnumerateArray().Select(folder => folder.GetProperty("guid").GetString()!).ToList(),
            Next("itemsNextOffset"),
            Next("foldersNextOffset"),
            ResponseBudget.SizeOf(response));
    }

    [Fact]
    public void LongFolderPathsDoNotCrowdOutItemsAtLimit200()
    {
        // Folders used to take the budget first: 111 folders, 0 items, itemsNextOffset = offset.
        var folders = Folders(300);
        var items = Items(500);

        Shown page = Render(Listing(folders, items, offset: 0, foldersOffset: 0, limit: 200));

        Assert.NotEmpty(page.ItemGuids);
        Assert.True(page.ItemsNextOffset > 0);
        Assert.Equal(page.ItemGuids.Count, page.ItemsNextOffset);
        Assert.True(page.Size <= MaxChars, $"{page.Size} characters at limit {MaxChars}");
    }

    [Fact]
    public void ItemsTakeTheBudgetFirstAndFoldersGetOnlyTheRemainder()
    {
        // Few items: there is room for folders too, but not for all 200.
        Shown page = Render(Listing(Folders(300), Items(5), offset: 0, foldersOffset: 0, limit: 200));

        Assert.Equal(5, page.ItemGuids.Count);
        Assert.Null(page.ItemsNextOffset);
        Assert.NotEmpty(page.FolderGuids);
        Assert.True(page.FolderGuids.Count < 200);
        Assert.Equal(page.FolderGuids.Count, page.FoldersNextOffset);
        Assert.True(page.Size <= MaxChars, $"{page.Size} characters at limit {MaxChars}");
    }

    [Fact]
    public void NoFolderFitsPointsFoldersNextOffsetAtTheSamePlace()
    {
        // Items took the whole budget: no folder fit, foldersNextOffset says where to continue from.
        Shown page = Render(Listing(Folders(30), Items(500), offset: 0, foldersOffset: 7, limit: 200));

        Assert.NotEmpty(page.ItemGuids);
        Assert.Empty(page.FolderGuids);
        Assert.Equal(7, page.FoldersNextOffset);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(50)]
    [InlineData(200)]
    public void WalkingItemsByNextOffsetReachesTheEnd(int limit)
    {
        var folders = Folders(300);
        var items = Items(450);

        var seen = new List<string>();
        int offset = 0;
        int pages = 0;

        while (true)
        {
            Shown page = Render(Listing(folders, items, offset, foldersOffset: 0, limit));

            Assert.NotEmpty(page.ItemGuids);
            Assert.True(page.Size <= MaxChars, $"{page.Size} characters at limit {MaxChars}");
            seen.AddRange(page.ItemGuids);
            pages++;

            if (page.ItemsNextOffset is not { } next)
            {
                break;
            }

            Assert.True(next > offset, $"itemsNextOffset {next} is not greater than offset {offset}");
            offset = next;
            Assert.True(pages < 1000, "the walk never ends");
        }

        Assert.Equal(items.Count, seen.Count);
        Assert.Equal(items.Select(item => item.Guid), seen);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(200)]
    public void WalkingFoldersByNextOffsetReachesTheEnd(int limit)
    {
        var folders = Folders(300);
        var items = Items(450);

        // Folders separately: offset past the end of items — the items page is empty, folders get the whole budget.
        var seen = new List<string>();
        int foldersOffset = 0;
        int pages = 0;

        while (true)
        {
            Shown page = Render(Listing(folders, items, offset: items.Count, foldersOffset, limit));

            Assert.Empty(page.ItemGuids);
            Assert.NotEmpty(page.FolderGuids);
            Assert.True(page.Size <= MaxChars, $"{page.Size} characters at limit {MaxChars}");
            seen.AddRange(page.FolderGuids);
            pages++;

            if (page.FoldersNextOffset is not { } next)
            {
                break;
            }

            Assert.True(next > foldersOffset, $"foldersNextOffset {next} is not greater than foldersOffset {foldersOffset}");
            foldersOffset = next;
            Assert.True(pages < 1000, "the walk never ends");
        }

        Assert.Equal(folders.Count, seen.Count);
        Assert.Equal(folders.Select(folder => folder.Guid), seen);
    }

    [Fact]
    public void WalkingBothListsTogetherReachesTheEndOfBoth()
    {
        // As the agent pages by the instructions: substitutes both offsets from the previous response at once.
        var folders = Folders(120);
        var items = Items(260);

        var seenItems = new List<string>();
        var seenFolders = new List<string>();
        int offset = 0;
        int foldersOffset = 0;

        for (int pages = 0; pages < 1000; pages++)
        {
            Shown page = Render(Listing(folders, items, offset, foldersOffset, limit: 200));

            Assert.True(page.Size <= MaxChars, $"{page.Size} characters at limit {MaxChars}");
            seenItems.AddRange(page.ItemGuids);
            seenFolders.AddRange(page.FolderGuids);

            int nextOffset = page.ItemsNextOffset ?? items.Count;
            int nextFoldersOffset = page.FoldersNextOffset ?? folders.Count;

            if (page.ItemsNextOffset is null && page.FoldersNextOffset is null)
            {
                Assert.Equal(items.Count, seenItems.Count);
                Assert.Equal(folders.Count, seenFolders.Count);
                return;
            }

            Assert.True(
                nextOffset > offset || nextFoldersOffset > foldersOffset,
                "no offset advanced — the agent would loop");

            offset = nextOffset;
            foldersOffset = nextFoldersOffset;
        }

        Assert.Fail("the walk never ends");
    }

    [Fact]
    public void WithoutItemsAtLeastOneFolderIsShown()
    {
        Shown page = Render(Listing(Folders(300), [], offset: 0, foldersOffset: 0, limit: 200));

        Assert.NotEmpty(page.FolderGuids);
        Assert.True(page.Size <= MaxChars, $"{page.Size} characters at limit {MaxChars}");
    }

    [Fact]
    public void ShortPageIsShownWholeWithoutNextOffsets()
    {
        Shown page = Render(Listing(Folders(3), Items(4), offset: 0, foldersOffset: 0, limit: 50));

        Assert.Equal(4, page.ItemGuids.Count);
        Assert.Equal(3, page.FolderGuids.Count);
        Assert.Null(page.ItemsNextOffset);
        Assert.Null(page.FoldersNextOffset);
    }

    [Fact]
    public void ResponseListsItemsBeforeFolders()
    {
        object response = TrashResponse.Build(Listing(Folders(2), Items(2), 0, 0, 50), MaxChars);
        string json = JsonSerializer.Serialize(response, McpJsonUtilities.DefaultOptions);

        Assert.True(json.IndexOf("\"items\"", StringComparison.Ordinal) < json.IndexOf("\"folders\"", StringComparison.Ordinal));
    }
}
