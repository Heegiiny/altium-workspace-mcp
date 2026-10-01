using AltiumWorkspaceMCP.Soap.Vault;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>A violation of the footprint set of a revision (vault_check_components).</summary>
/// <param name="Detail">What is wrong — for the agent.</param>
public sealed record FootprintSetViolation(string Detail);

/// <summary>
/// A refusal for one part: the primary footprint cannot be removed by the single role when the part has several
/// (additional ones would be left without a primary). The other parts of the call go on as usual.
/// </summary>
public sealed class FootprintSetException(int count) : InvalidOperationException(Describe("the part", count))
{
    /// <summary>How many footprints the part has.</summary>
    public int Count { get; } = count;

    /// <summary>The same refusal with the part named: "CMP-000-0960 has 3 footprints; …".</summary>
    public string DescribeFor(string component) => Describe(component, Count);

    private static string Describe(string owner, int count) =>
        $"{owner} has {count} footprints; to remove the primary, give the whole set with the footprints role "
        + "(the first in the list becomes primary) or an empty footprints list to remove all";
}

/// <summary>
/// The footprint set of a component revision: pure logic without server calls.
/// </summary>
/// <remarks>
/// This is how Altium Designer itself stores the set (a part with several footprints):
/// <list type="table">
/// <item><term>primary</term><description>role "PCBLIB", data <c>{"Footprint":{"FootprintIndex":0,"IsDefaultFootprint":true}}</c></description></item>
/// <item><term>additional #n</term><description>role "PCBLIB n", data <c>{"Footprint":{"FootprintIndex":n,"IsDefaultFootprint":false}}</c></description></item>
/// </list>
/// The number in the role matches FootprintIndex. The data is formed by <see cref="LinkNormalizer.FootprintData"/>.
/// </remarks>
public static class FootprintLinks
{
    /// <summary>Footprint links in number order: the primary first.</summary>
    public static List<ALU_ItemRevisionLink> Ordered(IEnumerable<ALU_ItemRevisionLink> links) =>
        links
            .Where(link => LinkRole.IsFootprint(link.HRID))
            .Select((link, position) => (Link: link, Position: position))
            .OrderBy(item => EffectiveIndex(item.Link))
            .ThenByDescending(item => LinkNormalizer.TryReadFootprint(item.Link.Data, out _, out bool isDefault) && isDefault)
            .ThenBy(item => item.Position)
            .Select(item => item.Link)
            .ToList();

    /// <summary>
    /// The footprint number of a link: FootprintIndex from the data, and without it — the number from the role
    /// («PCBLIB» — 0, «PCBLIB n» — n).
    /// </summary>
    public static int EffectiveIndex(ALU_ItemRevisionLink link) =>
        LinkNormalizer.TryReadFootprint(link.Data, out int index, out _)
            ? index
            : LinkRole.FootprintNumber(link.HRID) ?? int.MaxValue;

    /// <summary>
    /// Changes a footprint link: replaces the primary (<paramref name="append"/> = false) or adds
    /// an additional one under the next number. The other footprints are not touched.
    /// </summary>
    /// <returns>The description of the change or <see langword="null"/> if there is nothing to change.</returns>
    /// <exception cref="FootprintSetException">An empty target with several footprints.</exception>
    public static string? ApplySingle(
        List<ALU_ItemRevisionLink> links,
        string? target,
        bool append,
        string parentRevisionGuid)
    {
        var footprints = Ordered(links);

        if (append)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                throw new InvalidOperationException(
                    "To add a footprint a target is needed; footprints can be removed by the footprints role with a targets list.");
            }

            if (footprints.Any(link => SameTarget(link, target)))
            {
                return null;
            }

            int number = footprints.Count == 0 ? 0 : footprints.Max(link => Math.Max(EffectiveIndex(link), LinkRole.FootprintNumber(link.HRID) ?? 0)) + 1;
            links.Add(NewLink(parentRevisionGuid, target, number));

            return number == 0
                ? $"{LinkRole.Describe(LinkRole.Footprint)}: link added to {target}"
                : $"{LinkRole.Describe(LinkRole.FootprintRole(number))}: link added to {target}";
        }

        // Of several footprints the primary is replaced: the "PCBLIB" role as a whole, and for old links —
        // the one marked primary in the data.
        ALU_ItemRevisionLink? existing =
            footprints.FirstOrDefault(link => string.Equals(link.HRID?.Trim(), LinkRole.Footprint, StringComparison.OrdinalIgnoreCase))
            ?? footprints.FirstOrDefault(link => LinkNormalizer.TryReadFootprint(link.Data, out _, out bool isDefault) && isDefault)
            ?? footprints.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(target))
        {
            if (existing is null)
            {
                return null;
            }

            // Of several footprints the primary is not removed by the single role: additional ones would be left without a primary.
            if (footprints.Count > 1)
            {
                throw new FootprintSetException(footprints.Count);
            }

            links.Remove(existing);
            return $"{LinkRole.Describe(LinkRole.Footprint)}: link removed";
        }

        if (existing is null)
        {
            links.Add(new ALU_ItemRevisionLink
            {
                GUID = string.Empty,
                HRID = LinkRole.Footprint,
                ParentItemRevisionGUID = parentRevisionGuid,
                ChildItemRevisionGUID = target,
            });

            return $"{LinkRole.Describe(LinkRole.Footprint)}: link added to {target}";
        }

        if (SameTarget(existing, target))
        {
            return null;
        }

        string description =
            $"{LinkRole.Describe(LinkRole.Footprint)}: {existing.ChildItemRevisionGUID} → {target}";
        existing.ChildItemRevisionGUID = target;
        return description;
    }

    /// <summary>
    /// Replaces the whole footprint set: the first in the list is primary ("PCBLIB", index 0), the others are
    /// "PCBLIB 1…n" with indexes 1…n. Links whose target stayed in the set are reused (with their
    /// service fields), the others are removed.
    /// </summary>
    /// <returns>The description of the change or <see langword="null"/> if the set is already like that and written correctly.</returns>
    public static string? ApplySet(
        List<ALU_ItemRevisionLink> links,
        IReadOnlyList<string> targets,
        string parentRevisionGuid)
    {
        var existing = Ordered(links);

        if (IsCanonical(existing, targets))
        {
            return null;
        }

        int insertAt = existing.Count == 0 ? links.Count : existing.Min(link => links.IndexOf(link));
        var pool = existing.ToList();
        var result = new List<ALU_ItemRevisionLink>(targets.Count);

        for (int number = 0; number < targets.Count; number++)
        {
            string target = targets[number];

            ALU_ItemRevisionLink? link = existing.ElementAtOrDefault(number) is { } same && pool.Contains(same) && SameTarget(same, target)
                ? same
                : pool.FirstOrDefault(candidate => SameTarget(candidate, target));

            if (link is not null)
            {
                pool.Remove(link);
            }
            else
            {
                link = new ALU_ItemRevisionLink
                {
                    GUID = string.Empty,
                    ParentItemRevisionGUID = parentRevisionGuid,
                    ChildItemRevisionGUID = target,
                };
            }

            link.HRID = LinkRole.FootprintRole(number);
            link.Data = LinkNormalizer.FootprintData(number, isDefault: number == 0);
            result.Add(link);
        }

        links.RemoveAll(existing.Contains);
        links.InsertRange(Math.Min(insertAt, links.Count), result);

        return targets.Count == 0
            ? $"{LinkRole.Describe(LinkRole.Footprints)}: all links removed (there were {existing.Count})"
            : $"{LinkRole.Describe(LinkRole.Footprints)}: set replaced — there were {existing.Count}, now {targets.Count}; "
                + $"primary {targets[0]}";
    }

    /// <summary>
    /// The set is already written the way Altium would write it: the same targets in the same order, each with the role
    /// "PCBLIB" or "PCBLIB n" and data with FootprintIndex = n, only the first one is primary.
    /// </summary>
    /// <param name="existing">Footprint links in number order (<see cref="Ordered"/>).</param>
    public static bool IsCanonical(IReadOnlyList<ALU_ItemRevisionLink> existing, IReadOnlyList<string> targets)
    {
        if (existing.Count != targets.Count)
        {
            return false;
        }

        for (int number = 0; number < targets.Count; number++)
        {
            ALU_ItemRevisionLink link = existing[number];

            if (!SameTarget(link, targets[number])
                || !string.Equals(link.HRID?.Trim(), LinkRole.FootprintRole(number), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(link.Data, LinkNormalizer.FootprintData(number, isDefault: number == 0), StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether the revision link set is changed by the assignment <paramref name="assignment"/> (the footprint or footprints role).</summary>
    public static bool Changes(LinkAssignment assignment, IEnumerable<ALU_ItemRevisionLink> links)
    {
        // Assembling the links is the only source of truth about what will change.
        var copy = links.Select(link => new ALU_ItemRevisionLink
        {
            GUID = link.GUID,
            HRID = link.HRID,
            ParentItemRevisionGUID = link.ParentItemRevisionGUID,
            ChildItemRevisionGUID = link.ChildItemRevisionGUID,
            Data = link.Data,
        }).ToList();

        return Apply(copy, assignment, string.Empty) is not null;
    }

    /// <summary>Applies the footprint or footprints role assignment to the link list.</summary>
    public static string? Apply(List<ALU_ItemRevisionLink> links, LinkAssignment assignment, string parentRevisionGuid)
    {
        string role = LinkRole.Normalize(assignment.Role);

        if (string.Equals(role, LinkRole.Footprints, StringComparison.OrdinalIgnoreCase))
        {
            var targets = (assignment.TargetRevisionGuids ?? [])
                .Where(target => !string.IsNullOrWhiteSpace(target))
                .Select(target => target.Trim())
                .ToList();

            return ApplySet(links, targets, parentRevisionGuid);
        }

        return ApplySingle(links, assignment.TargetRevisionGuid, assignment.Append, parentRevisionGuid);
    }

    /// <summary>
    /// Set violations: two primaries, no primary among several, a repeated number, a "PCBLIB n" role with
    /// FootprintIndex other than n. Fixes nothing. A link without data has nothing to check — it is a separate
    /// problem (<see cref="ComponentHealthService.Kinds.FootprintData"/>).
    /// </summary>
    public static IReadOnlyList<FootprintSetViolation> Check(IEnumerable<ALU_ItemRevisionLink> links)
    {
        var violations = new List<FootprintSetViolation>();
        var footprints = Ordered(links);
        var described = footprints
            .Select(link => (Link: link, Known: LinkNormalizer.TryReadFootprint(link.Data, out int index, out bool isDefault), Index: index, IsDefault: isDefault))
            .Where(item => item.Known)
            .ToList();

        var defaults = described.Where(item => item.IsDefault).ToList();

        if (defaults.Count > 1)
        {
            violations.Add(new FootprintSetViolation(
                $"{defaults.Count} primary footprints ({string.Join(", ", defaults.Select(item => Label(item.Link)))}): "
                + "there must be exactly one primary"));
        }
        else if (defaults.Count == 0 && described.Count > 1)
        {
            violations.Add(new FootprintSetViolation(
                $"{described.Count} footprints with no primary (IsDefaultFootprint=true on none)"));
        }

        foreach (var group in described.GroupBy(item => item.Index).Where(group => group.Count() > 1))
        {
            violations.Add(new FootprintSetViolation(
                $"FootprintIndex={group.Key} is repeated in {group.Count()} footprints "
                + $"({string.Join(", ", group.Select(item => Label(item.Link)))})"));
        }

        foreach (var item in described)
        {
            int? number = LinkRole.FootprintNumber(item.Link.HRID);

            if (number is not null && number.Value != item.Index)
            {
                violations.Add(new FootprintSetViolation(
                    $"link '{item.Link.HRID}' to {item.Link.ChildItemRevisionGUID} is written with FootprintIndex={item.Index}, "
                    + $"but should be {number.Value}"));
            }
        }

        return violations;
    }

    private static string Label(ALU_ItemRevisionLink link) => $"«{link.HRID}» → {link.ChildItemRevisionGUID}";

    private static bool SameTarget(ALU_ItemRevisionLink link, string target) =>
        string.Equals(link.ChildItemRevisionGUID, target, StringComparison.OrdinalIgnoreCase);

    private static ALU_ItemRevisionLink NewLink(string parentRevisionGuid, string target, int number) => new()
    {
        GUID = string.Empty,
        HRID = LinkRole.FootprintRole(number),
        ParentItemRevisionGUID = parentRevisionGuid,
        ChildItemRevisionGUID = target,
        Data = LinkNormalizer.FootprintData(number, isDefault: number == 0),
    };
}
