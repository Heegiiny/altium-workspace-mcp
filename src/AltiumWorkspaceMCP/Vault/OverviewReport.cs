using AltiumWorkspaceMCP.Tools;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>Response of <c>vault_components action=overview</c>: a compact view of the summary within the character budget.</summary>
public static class OverviewReport
{
    public static object Build(OverviewResult result, PanelQuery query, int maxChars)
    {
        DocumentTally tally = result.Tally;
        long total = result.Total;
        var hints = new List<string>();

        var folders = TypeOverview.TopWithRest(tally.Folders.Select(pair => KeyValuePair.Create(pair.Key, pair.Value)), TypeOverview.TopFolders);

        object gaps = new
        {
            withoutMpn = TypeOverview.Share(tally.Gaps.WithoutMpn, tally.Documents),
            withoutLcscPart = TypeOverview.Share(tally.Gaps.WithoutLcsc, tally.Documents),
            withoutFootprint = TypeOverview.Share(tally.Gaps.WithoutFootprint, tally.Documents),
        };

        IReadOnlyList<string>? byType = null;
        IReadOnlyList<string>? parameters = null;
        string? rare = null;

        if (result.Scope.Type is null)
        {
            byType = tally.TopTypes
                .Select(line => $"{line.Type} — {line.Total}, no MPN {TypeOverview.Share(line.WithoutMpn, line.Total)}")
                .ToList();
            hints.Add("Parameters and fill rates — for one type: action=overview type=<path> (the type tree — action=types).");
        }
        else
        {
            (IReadOnlyList<OverviewParameter> shown, int rareCount) = TypeOverview.FoldRare(result.Parameters, total);

            string header = $"{result.Scope.Type}: {total} parts";
            int reserved = ResponseBudget.SizeOf(folders) + ResponseBudget.SizeOf(gaps) + header.Length + 1200;
            var budget = new ResponseBudget(maxChars, reserved);
            var lines = budget.TakeFitting(shown.Select(parameter => Line(parameter, total)));

            int cut = shown.Count - lines.Count;
            parameters = lines;
            rare = rareCount > 0 ? $"rare: {rareCount} parameters (filled in less than {TypeOverview.RareShare * 100:0}%)" : null;

            if (cut > 0)
            {
                hints.Add($"The response is limited by a budget of {maxChars} characters: {cut} parameters are not shown; the full list — action=facets type=<path> columns=[…].");
            }
        }

        if (result.Truncated)
        {
            hints.Add($"The selection is larger than {tally.Documents}: folders and gaps are counted by the first {tally.Documents} parts. Narrow type or folder.");
        }

        if (tally.WithoutType > 0 && result.Scope.Type is null)
        {
            hints.Add($"Parts without a type: {tally.WithoutType} (not included in the top types; vault_component_types will assign a type).");
        }

        hints.Add("The component template is not stored in the search index: for templates — vault_templates / vault_component_detail.");

        return new
        {
            type = result.Scope.Type,
            folder = result.Scope.Folder,
            resolvedFolder = result.Scope.FolderMatch?.Describe(query.Folder ?? string.Empty),
            excludedStates = result.Scope.ExcludedStates.Count > 0 ? string.Join(", ", result.Scope.ExcludedStates) : null,
            total,
            elapsedMs = result.ElapsedMs,
            gaps,
            folders,
            byType,
            parameters,
            rare,
            hint = string.Join(" ", hints),
        };
    }

    /// <summary>«Value — 1995 (98.5%): 10k (12), 1k (9)».</summary>
    public static string Line(OverviewParameter parameter, long total) =>
        $"{parameter.Name} — {TypeOverview.Share(parameter.Filled, total)}"
        + (parameter.Top.Count > 0 ? $": {string.Join(", ", parameter.Top)}" : string.Empty);
}
