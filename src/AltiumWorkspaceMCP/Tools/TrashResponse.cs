using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Tools;

/// <summary>
/// Response of <c>vault_restore_items action=list</c>: a trash page fitted into the size budget
/// (pure logic, no server calls).
/// </summary>
internal static class TrashResponse
{
    /// <summary>How many characters are reserved for everything outside the rows: header, offsets, hint.</summary>
    public const int Reserved = 3000;

    private const string Note =
        "Restore: vault_restore_items action=restore itemGuids=[…] and/or folders=[…] "
        + "(a folder GUID restores it with its contents). deletedAt is the deletion time of the nearest deleted "
        + "ancestor folder (then deletedWithFolder is its path) or, if neither the folder nor its ancestors were "
        + "deleted separately, the last-edit time of the object itself (the server does not keep "
        + "a separate deletion time). Items come first, folders take the rest of the budget: if "
        + "they did not fit, page through the items, and when itemsNextOffset becomes null the folders get "
        + "the whole response (or use offset=itemsTotal at once — folders only). To continue paging — the same call "
        + "with offset=itemsNextOffset and/or foldersOffset=foldersNextOffset; null — everything "
        + "selected by this listing has been shown.";

    /// <summary>
    /// Fits the page into <paramref name="maxChars"/>. Items — the main content — get the budget
    /// first, folders get the rest: so long folder paths do not push out items (with limit=200 folders
    /// once filled the whole response while itemsNextOffset stayed the same — the agent looped).
    /// </summary>
    /// <remarks>
    /// Guarantees: if the page has items, at least one is shown, and itemsNextOffset (when not
    /// empty) is greater than offset; if the page has no items, at least one folder is shown (when there are any).
    /// The exception is <c>timedOut</c>: the usage count did not manage even the first item, and itemsNextOffset
    /// equals offset — continue with the same call. If no folder fit, foldersNextOffset
    /// equals foldersOffset.
    /// </remarks>
    public static object Build(TrashListing listing, int maxChars)
    {
        var budget = new ResponseBudget(maxChars, Reserved);

        var items = budget.TakeFitting(listing.Items.Select(item => (object)new
        {
            guid = item.Guid,
            hrid = item.Hrid,
            contentType = item.ContentType,
            folder = item.RestoredFolderPath,
            deletedAt = item.DeletedAt,
            deletedWithFolder = item.DeletedWithFolderPath,
            usedByComponents = new { count = item.UsedByComponents.Count, hrids = item.UsedByComponents.SampleHrids },
            usedByTemplates = new { count = item.UsedByTemplates.Count, hrids = item.UsedByTemplates.SampleHrids },
        }));

        var folders = budget.TakeFitting(listing.Folders.Select(folder => (object)new
        {
            guid = folder.Guid,
            path = folder.RestoredPath,
            deletedAt = folder.DeletedAt,
        }));

        // The budget cut the page earlier than offset/limit did: the next offset of the previous calculation
        // (by offset/limit, not by characters) would not match what was actually shown — step back to what was shown.
        int? foldersNextOffset = folders.Count < listing.Folders.Count
            ? listing.FoldersOffset + folders.Count
            : listing.FoldersNextOffset;

        int? itemsNextOffset = items.Count < listing.Items.Count
            ? listing.ItemsOffset + items.Count
            : listing.ItemsNextOffset;

        return new
        {
            items,
            itemsTotal = listing.ItemsTotal,
            itemsOffset = listing.ItemsOffset,
            itemsNextOffset,
            folders,
            foldersTotal = listing.FoldersTotal,
            foldersOffset = listing.FoldersOffset,
            foldersNextOffset,
            timedOut = listing.TimedOut,
            note = Note,
        };
    }
}
