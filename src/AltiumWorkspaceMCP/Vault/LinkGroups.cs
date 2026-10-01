namespace AltiumWorkspaceMCP.Vault;

/// <summary>
/// An assigned link: a role and a target (empty — the link is removed). For the "footprints" role the targets are in <paramref name="Targets"/>:
/// the whole footprint set, the first is primary (an empty list removes all).
/// </summary>
public sealed record LinkTargetSpec(string Role, string? Target, IReadOnlyList<string>? Targets = null);

/// <summary>One relink group: these components get these links.</summary>
public sealed record LinkGroupSpec(IReadOnlyList<string> Components, IReadOnlyList<LinkTargetSpec> Links);

/// <summary>A component to which two groups assign the same role.</summary>
public sealed record LinkOverlap(string Component, string Role, IReadOnlyList<int> Groups);

/// <summary>
/// Parsing and checking of relink groups (vault_set_links): pure logic without server calls.
/// </summary>
public static class LinkGroups
{
    /// <summary>
    /// Checks the groups: they exist, each has components and links, a role is not repeated in a group.
    /// Returns the groups with the roles brought to the server code.
    /// </summary>
    public static IReadOnlyList<LinkGroupSpec> Validate(IReadOnlyList<LinkGroupSpec> groups)
    {
        if (groups.Count == 0)
        {
            throw new ArgumentException(
                "Specify the components and the links to assign: either components and links, "
                + "or assignments — a list of groups {components, links}.");
        }

        var result = new List<LinkGroupSpec>(groups.Count);

        for (int index = 0; index < groups.Count; index++)
        {
            LinkGroupSpec group = groups[index];
            string name = $"Group {index + 1}";

            if (group.Components.Count == 0 || group.Components.All(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException($"{name}: no components specified.");
            }

            if (group.Links.Count == 0)
            {
                throw new ArgumentException($"{name}: no links specified (links).");
            }

            var links = group.Links
                .Select(link => new LinkTargetSpec(LinkRole.Parse(link.Role), link.Target, link.Targets))
                .ToList();

            foreach (LinkTargetSpec link in links)
            {
                ValidateFootprintRole(name, link);
            }

            var repeated = links
                .GroupBy(link => RoleKey(link.Role), StringComparer.OrdinalIgnoreCase)
                .Where(byRole => byRole.Count() > 1)
                .Select(byRole => LinkRole.Describe(byRole.Key))
                .ToList();

            if (repeated.Count > 0)
            {
                throw new ArgumentException(
                    $"{name}: the role is given twice ({string.Join(", ", repeated)}). "
                    + "One group — one target per role; for different targets create different groups.");
            }

            result.Add(new LinkGroupSpec(
                group.Components.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()).ToList(),
                links));
        }

        return result;
    }

    /// <summary>
    /// A role for comparing overlaps: "footprint" and "footprints" assign the same thing — footprints,
    /// so together in one group, or on one component in different groups, they conflict.
    /// </summary>
    public static string RoleKey(string role) =>
        string.Equals(role, LinkRole.Footprints, StringComparison.OrdinalIgnoreCase) ? LinkRole.Footprint : role;

    /// <summary>Assignment targets without empty ones and edge spaces: one target or the whole footprint set.</summary>
    public static IEnumerable<string> TargetsOf(LinkTargetSpec link) =>
        string.Equals(link.Role, LinkRole.Footprints, StringComparison.OrdinalIgnoreCase)
            ? (link.Targets ?? []).Where(target => !string.IsNullOrWhiteSpace(target)).Select(target => target.Trim())
            : string.IsNullOrWhiteSpace(link.Target) ? [] : [link.Target.Trim()];

    /// <summary>
    /// Footprint roles: footprints — a set in targets (no empty targets or repeats), footprint — one target
    /// in target, and "PCBLIB n" is not assigned directly.
    /// </summary>
    public static void ValidateFootprintRole(string group, LinkTargetSpec link)
    {
        if (string.Equals(link.Role, LinkRole.Footprints, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(link.Target))
            {
                throw new ArgumentException(
                    $"{group}: for the footprints role the targets are given as a targets list, not target. "
                    + "For example: {\"role\":\"footprints\",\"targets\":[\"PCC-…\",\"PCC-…\"]} — the first is primary.");
            }

            if (link.Targets is null)
            {
                throw new ArgumentException(
                    $"{group}: the footprints role needs a targets list — the whole footprint set, the first is primary. "
                    + "An empty list [] removes all footprints; to change the single primary footprint use the footprint role with target.");
            }

            if (link.Targets.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException(
                    $"{group}: targets has an empty target. Specify footprint identifiers (PCC-…) or revision GUIDs; "
                    + "to remove all footprints, pass an empty list [].");
            }

            var repeatedTargets = link.Targets
                .Select(target => target.Trim())
                .GroupBy(target => target, StringComparer.OrdinalIgnoreCase)
                .Where(byTarget => byTarget.Count() > 1)
                .Select(byTarget => byTarget.Key)
                .ToList();

            if (repeatedTargets.Count > 0)
            {
                throw new ArgumentException(
                    $"{group}: a footprint is repeated in targets ({string.Join(", ", repeatedTargets)}). "
                    + "Each footprint is in the set once.");
            }

            return;
        }

        if (link.Targets is { Count: > 0 })
        {
            throw new ArgumentException(
                $"{group}: targets is set only for the footprints role. For the role '{LinkRole.Describe(link.Role)}' use target.");
        }

        if (LinkRole.IsFootprint(link.Role) && !string.Equals(link.Role, LinkRole.Footprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"{group}: the role '{link.Role}' is not assigned directly — the number of an additional footprint is issued in order. "
                + "Set the whole set with the footprints role and a targets list.");
        }
    }

    /// <summary>
    /// Components that fell into several groups with the same role: which target to assign to them is unclear.
    /// </summary>
    /// <param name="groups">Groups with the roles brought to the server code.</param>
    /// <param name="key">
    /// How to recognize the same component under different names (identifier and GUID);
    /// by default — the name itself, case-insensitive.
    /// </param>
    public static IReadOnlyList<LinkOverlap> FindOverlaps(
        IReadOnlyList<LinkGroupSpec> groups,
        Func<string, string>? key = null)
    {
        key ??= item => item.ToUpperInvariant();

        var seen = new Dictionary<(string Component, string Role), List<int>>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        for (int index = 0; index < groups.Count; index++)
        {
            foreach (string component in groups[index].Components)
            {
                string id = key(component);
                names.TryAdd(id, component);

                foreach (LinkTargetSpec link in groups[index].Links)
                {
                    string role = RoleKey(link.Role).ToUpperInvariant();

                    var owners = seen.TryGetValue((id, role), out var list)
                        ? list
                        : seen[(id, role)] = [];

                    if (!owners.Contains(index))
                    {
                        owners.Add(index);
                    }
                }
            }
        }

        return seen
            .Where(entry => entry.Value.Count > 1)
            .Select(entry => new LinkOverlap(
                names[entry.Key.Component],
                LinkRole.Describe(groups.SelectMany(group => group.Links)
                    .First(link => string.Equals(RoleKey(link.Role), entry.Key.Role, StringComparison.OrdinalIgnoreCase)).Role),
                entry.Value.Select(group => group + 1).ToList()))
            .ToList();
    }

    /// <summary>How many different components the groups affect: one revision per each.</summary>
    public static int CountAffected(IReadOnlyList<LinkGroupSpec> groups, Func<string, string>? key = null)
    {
        key ??= item => item.ToUpperInvariant();

        return groups.SelectMany(group => group.Components).Select(key).Distinct(StringComparer.Ordinal).Count();
    }

    /// <summary>Refusal text on overlapping groups — with the list of components and the group numbers.</summary>
    public static string DescribeOverlaps(IReadOnlyList<LinkOverlap> overlaps, int shown = 20) =>
        $"Components fell into several groups with the same role ({overlaps.Count}): "
        + string.Join("; ", overlaps.Take(shown).Select(overlap =>
            $"{overlap.Component} — {overlap.Role}, groups {string.Join(" and ", overlap.Groups)}"))
        + (overlaps.Count > shown ? $"; and {overlaps.Count - shown} more" : string.Empty)
        + ". Keep each component in one group per role and repeat the call.";
}
