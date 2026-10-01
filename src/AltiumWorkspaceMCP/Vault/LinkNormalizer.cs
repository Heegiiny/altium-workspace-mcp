using System.Text.Json;
using AltiumWorkspaceMCP.Soap.Vault;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>A correction made to a revision link.</summary>
/// <param name="RevisionGuid">The revision the link belongs to.</param>
public sealed record LinkCorrection(string RevisionGuid, string Text);

/// <summary>
/// Brings revision links to the form in which Altium Designer itself saves them.
/// </summary>
/// <remarks>
/// The server accepts a link even without some fields, but Altium Designer does not understand such a link.
/// Comparing a revision released by the MCP server with a revision of the same component saved
/// from Single Component Editor showed the mandatory features:
/// <list type="bullet">
/// <item>the role in the HRID field — Altium uses it to tell a symbol from a footprint;</item>
/// <item>the vault GUID on the parent and on the child revision;</item>
/// <item>for a footprint — the data <c>{"Footprint":{"FootprintIndex":…,"IsDefaultFootprint":…}}</c>:
/// without it the footprint is not visible in Altium although the link exists on the server.</item>
/// </list>
/// The rules repeat how Single Component Editor writes links.
/// </remarks>
public sealed class LinkNormalizer
{
    private static readonly Dictionary<string, string> RolesByContentType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["altium-symbol"] = LinkRole.Symbol,
        ["altium-pcb-component"] = LinkRole.Footprint,
        ["altium-component-template"] = LinkRole.Template,
        ["altium-datasheet"] = LinkRole.Datasheet,
        ["altium-simulation-model"] = LinkRole.Simulation,
    };

    private readonly VaultGateway _gateway;
    private readonly VaultCatalog _catalog;

    public LinkNormalizer(VaultGateway gateway, VaultCatalog catalog)
    {
        _gateway = gateway;
        _catalog = catalog;
    }

    /// <summary>Supplements links with the missing features and returns the list of corrections made.</summary>
    public async Task<IReadOnlyList<LinkCorrection>> NormalizeAsync(
        IReadOnlyCollection<ALU_ItemRevisionLink> links,
        CancellationToken cancellationToken)
    {
        var corrections = new List<LinkCorrection>();

        if (links.Count == 0)
        {
            return corrections;
        }

        await RestoreMissingRolesAsync(links, corrections, cancellationToken);

        string vaultGuid = await _gateway.GetVaultGuidAsync(cancellationToken);

        foreach (ALU_ItemRevisionLink link in links)
        {
            bool fixedVault = false;

            if (string.IsNullOrWhiteSpace(link.ParentVaultGUID))
            {
                link.ParentVaultGUID = vaultGuid;
                fixedVault = true;
            }

            if (string.IsNullOrWhiteSpace(link.ChildVaultGUID))
            {
                link.ChildVaultGUID = vaultGuid;
                fixedVault = true;
            }

            // Altium writes empty strings, not absent values.
            link.Data ??= string.Empty;
            link.LinkTypeGUID ??= string.Empty;

            if (fixedVault)
            {
                corrections.Add(new LinkCorrection(
                    link.ParentItemRevisionGUID ?? string.Empty,
                    $"{LinkRole.Describe(link.HRID)}: vault GUID set"));
            }
        }

        ApplyModelData(links, corrections);
        return corrections;
    }

    /// <summary>Footprint link data exactly as Altium writes it.</summary>
    public static string FootprintData(int index, bool isDefault) =>
        "{\"Footprint\":{\"FootprintIndex\":" + index + ",\"IsDefaultFootprint\":" + (isDefault ? "true" : "false") + "}}";

    /// <summary>The link role by the content type of the object it points to; null — not a model.</summary>
    public static string? RoleForContentType(string? contentType) =>
        contentType is not null && RolesByContentType.TryGetValue(contentType, out string? role) ? role : null;

    /// <summary>
    /// Sets the role on links that have none.
    /// </summary>
    /// <remarks>
    /// Called before assignments are matched with links: earlier Altium versions wrote
    /// links with an empty role, and a footprint with such a link would otherwise not be found by role —
    /// a replacement would add a second one instead of replacing the existing one.
    /// </remarks>
    public async Task<IReadOnlyList<LinkCorrection>> RestoreRolesAsync(
        IReadOnlyCollection<ALU_ItemRevisionLink> links,
        CancellationToken cancellationToken)
    {
        var corrections = new List<LinkCorrection>();
        await RestoreMissingRolesAsync(links, corrections, cancellationToken);
        return corrections;
    }

    /// <summary>Reads the number and the primary flag of a footprint from the link data.</summary>
    public static bool TryReadFootprint(string? data, out int index, out bool isDefault)
    {
        index = 0;
        isDefault = false;

        if (string.IsNullOrWhiteSpace(data))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(data);

            if (!document.RootElement.TryGetProperty("Footprint", out JsonElement footprint)
                || footprint.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (footprint.TryGetProperty("FootprintIndex", out JsonElement indexElement)
                && indexElement.TryGetInt32(out int parsed))
            {
                index = parsed;
            }

            if (footprint.TryGetProperty("IsDefaultFootprint", out JsonElement defaultElement)
                && defaultElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                isDefault = defaultElement.GetBoolean();
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Numbers the revision's footprints and marks the primary. Already filled data
    /// is not touched: the numbers of new links are chosen so as not to match the existing ones —
    /// across all the revision's footprints, both "PCBLIB" and "PCBLIB n".
    /// </summary>
    /// <remarks>
    /// A link without data gets the number from its role ("PCBLIB n" — n) if it is free, otherwise the smallest
    /// free one; the primary ("PCBLIB") goes first and becomes the primary footprint if there is none.
    /// When the number is not zero, the role is brought to "PCBLIB n": this is how Altium stores additional footprints,
    /// and the role does not diverge from FootprintIndex.
    /// </remarks>
    internal static void ApplyModelData(IReadOnlyCollection<ALU_ItemRevisionLink> links, List<LinkCorrection> corrections)
    {
        foreach (var group in links.GroupBy(link => link.ParentItemRevisionGUID ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            var footprints = group
                .Where(link => LinkRole.IsFootprint(link.HRID))
                .ToList();

            var usedIndexes = new HashSet<int>();
            bool hasDefault = false;

            foreach (ALU_ItemRevisionLink footprint in footprints)
            {
                if (TryReadFootprint(footprint.Data, out int index, out bool isDefault))
                {
                    usedIndexes.Add(index);
                    hasDefault |= isDefault;
                }
            }

            // Non-empty but unclear data is left as is: it is not our markup.
            // The primary link ("PCBLIB") is processed first, the others — in the order of the numbers in the role.
            var undescribed = footprints
                .Where(link => string.IsNullOrWhiteSpace(link.Data))
                .Select((link, position) => (Link: link, Position: position))
                .OrderBy(item => LinkRole.FootprintNumber(item.Link.HRID) == 0 ? 0 : 1)
                .ThenBy(item => LinkRole.FootprintNumber(item.Link.HRID))
                .ThenBy(item => item.Position)
                .Select(item => item.Link)
                .ToList();

            foreach (ALU_ItemRevisionLink footprint in undescribed)
            {
                int wanted = LinkRole.FootprintNumber(footprint.HRID) ?? 0;
                int index = wanted;

                while (usedIndexes.Contains(index))
                {
                    index++;
                }

                // Only a link with the role "PCBLIB" becomes primary: an additional one with a number cannot be.
                bool isMain = LinkRole.FootprintNumber(footprint.HRID) == 0;
                usedIndexes.Add(index);
                bool isDefault = isMain && !hasDefault;
                hasDefault |= isDefault;

                string? renamed = null;
                string role = LinkRole.FootprintRole(index);

                if (!string.Equals(footprint.HRID?.Trim(), role, StringComparison.OrdinalIgnoreCase))
                {
                    renamed = $"; role '{footprint.HRID}' → '{role}'";
                    footprint.HRID = role;
                }

                footprint.Data = FootprintData(index, isDefault);
                corrections.Add(new LinkCorrection(
                    group.Key,
                    $"footprint: data assigned FootprintIndex={index}, "
                        + $"IsDefaultFootprint={(isDefault ? "true" : "false")}{renamed}"));
            }

            foreach (ALU_ItemRevisionLink simulation in group.Where(link =>
                         string.Equals(link.HRID, LinkRole.Simulation, StringComparison.OrdinalIgnoreCase)
                         && string.IsNullOrWhiteSpace(link.Data)))
            {
                simulation.Data = FootprintData(0, isDefault: false);
                corrections.Add(new LinkCorrection(group.Key, "simulation model: link data assigned"));
            }
        }
    }

    /// <summary>
    /// Sets the role on links that lack it, determining it by the type
    /// of the object the link points to.
    /// </summary>
    private async Task RestoreMissingRolesAsync(
        IReadOnlyCollection<ALU_ItemRevisionLink> links,
        List<LinkCorrection> corrections,
        CancellationToken cancellationToken)
    {
        var unresolved = links.Where(link => !IsKnownRole(link.HRID)).ToList();

        var targets = unresolved
            .Select(link => link.ChildItemRevisionGUID)
            .Where(guid => !string.IsNullOrEmpty(guid))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (targets.Count == 0)
        {
            return;
        }

        var revisions = await VaultGateway.ReadInChunksAsync(
            targets,
            chunk => _gateway.GetItemRevisionsAsync(
                VaultFilter.In("GUID", chunk), limit: 100000, cancellationToken: cancellationToken));

        var contentTypes = await _catalog.GetContentTypesAsync(cancellationToken);

        var typeByRevision = revisions
            .GroupBy(revision => revision.GUID, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => contentTypes.TryGetValue(group.First().ContentTypeGUID ?? string.Empty, out ALU_ContentType? type)
                    ? type.HRID
                    : null,
                StringComparer.OrdinalIgnoreCase);

        foreach (ALU_ItemRevisionLink link in unresolved)
        {
            if (typeByRevision.TryGetValue(link.ChildItemRevisionGUID ?? string.Empty, out string? contentType)
                && contentType is not null
                && RolesByContentType.TryGetValue(contentType, out string? role))
            {
                corrections.Add(new LinkCorrection(
                    link.ParentItemRevisionGUID ?? string.Empty,
                    $"role {role} restored on the link to {link.ChildItemRevisionGUID}"));
                link.HRID = role;
            }
        }
    }

    /// <summary>A value is recognized as a role if it matches one of the known ones.</summary>
    internal static bool IsKnownRole(string? hrid) =>
        !string.IsNullOrWhiteSpace(hrid)
        && (RolesByContentType.Values.Contains(hrid, StringComparer.OrdinalIgnoreCase) || LinkRole.IsFootprint(hrid));
}
