using System.ComponentModel;
using AltiumWorkspaceMCP.Safety;
using AltiumWorkspaceMCP.Vault;
using ModelContextProtocol.Server;

namespace AltiumWorkspaceMCP.Tools;

/// <summary>
/// Checking and fixing components so that Altium Designer reads them correctly
/// and does not damage them on save.
/// </summary>
[McpServerToolType]
public sealed class QualityTools
{
    private const string CheckOperation = "vault_check_components";
    private const string CleanupOperation = "vault_cleanup_parameters";

    /// <summary>How many components are listed by name in the response with their list of mismatches.</summary>
    private const int DetailedComponents = 100;

    private readonly VaultWorkspace _workspace;

    public QualityTools(VaultWorkspace workspace) => _workspace = workspace;

    [McpServerTool(Name = "vault_check_components", Destructive = true, Idempotent = true)]
    [Description("""
        Checks that Altium Designer will read components correctly and will not damage them on
        save, and on request fixes what it finds.

        Finds: typed parameters without a number (Altium clears them on a manual
        save of a component); values already cleared that way in past revisions; numbers that do not
        match the value; values that do not parse as a quantity of their
        type; footprint links without FootprintIndex (Altium will not show the footprint);
        links without a vault GUID or without a role; **parts without a component type** (they are
        not visible in the Components panel tree); **a reference to a model from the trash** — the link is intact, but the symbol, footprint or template was deleted; fix does not repair it,
        the hint names vault_restore_items to bring the model back.

        Selection: a components list or the folder folder (recursive — with nested ones) and the hrid pattern.
        fix=true releases a new revision for the fixable components: the same values, numbers and
        links appended by Altium rules, cleared values restored from history.
        The component type is assigned separately, by a tag, without a new revision: the one given by the part
        template, and if it has none — the default template of its folder; if there is no source,
        the part is marked unfixable — assign the type manually (vault_component_types assign).
        dryRun=true writes nothing and shows what was read and what would be written.
        """)]
    public Task<object> CheckAsync(
        [Description("Components: identifiers or GUIDs. Empty — select by folder and hrid.")]
        string[]? components = null,
        [Description("Folder: full path, path ending, name or GUID.")]
        string? folder = null,
        [Description("Include nested folders.")]
        bool recursive = false,
        [Description("Identifier pattern with %, for example CMP-016-%.")]
        string? hrid = null,
        [Description("Content type; default altium-component.")]
        string? contentType = null,
        [Description("Look in the revision history for values already cleared on save in Altium.")]
        bool history = true,
        [Description("Fix what was found with new revisions.")]
        bool fix = false,
        [Description("Dry run: read everything, write nothing, show the write plan.")]
        bool dryRun = false,
        [Description("Maximum components to check when selecting by folder.")]
        int limit = 2000,
        [Description("""
            From which position to show the detailed list of components with mismatches (100 per
            call). Take nextOffset from the previous response; the checks and fix do not depend on it.
            """)]
        int offset = 0,
        [Description("Note that goes into the revision release history.")]
        string releaseNote = "Fix parameter and link format for Altium",
        [Description("Confirmation token: needed to apply fix for more than 4 components after the preview. The other parameters are not needed then.")]
        string? confirmToken = null,
        CancellationToken cancellationToken = default) =>
        DryRun.RunAsync(
            CheckOperation,
            dryRun,
            () => _workspace.Guard.RunAsync(
                CheckOperation,
                confirmToken,
                (gate, ct) => CheckCoreAsync(
                    components, folder, recursive, hrid, contentType, history, fix, limit, offset, releaseNote, gate, ct),
                cancellationToken));

    /// <summary>How many edit results are listed by name; the rest as a count.</summary>
    private const int ResultsShown = 50;

    /// <summary>
    /// A page of the detailed list: at most <see cref="DetailedComponents"/> items and no
    /// more than half of the response budget, the rest — for another call with <c>nextOffset</c>.
    /// </summary>
    private (List<object> Page, int? NextOffset) PageOf<T>(IReadOnlyList<T> items, int offset, Func<T, object> project)
    {
        var budget = new ResponseBudget(_workspace.Options.MaxResponseChars / 2);
        var page = new List<object>();
        int next = Math.Max(offset, 0);

        foreach (T item in items.Skip(next).Take(DetailedComponents))
        {
            object view = project(item);
            if (!budget.TryAdd(view))
            {
                break;
            }

            page.Add(view);
            next++;
        }

        return (page, next < items.Count ? next : null);
    }

    /// <summary>The smaller the number, the more noticeable the mismatch in Altium.</summary>
    private static int Severity(string kind) => kind switch
    {
        ComponentHealthService.Kinds.ModelInTrash => 0,
        ComponentHealthService.Kinds.FootprintData => 0,
        ComponentHealthService.Kinds.FootprintSet => 1,
        ComponentHealthService.Kinds.UnknownRole => 1,
        ComponentHealthService.Kinds.VaultGuid => 2,
        ComponentHealthService.Kinds.ClearedValue => 3,
        ComponentHealthService.Kinds.InvalidValue => 4,
        ComponentHealthService.Kinds.StaleNumber => 5,
        ComponentHealthService.Kinds.MissingComponentType => 6,
        _ => 7,
    };

    private async Task<object> CheckCoreAsync(
        string[]? components,
        string? folder,
        bool recursive,
        string? hrid,
        string? contentType,
        bool history,
        bool fix,
        int limit,
        int offset,
        string releaseNote,
        ConfirmationGate gate,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ComponentRecord> records;
        object? resolvedFolder = null;

        if (components is { Length: > 0 })
        {
            records = await _workspace.Components.ReadByIdsAsync(components, cancellationToken);

            await _workspace.Components.EnsureFoundAsync(components, records, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(folder) || !string.IsNullOrWhiteSpace(hrid))
        {
            var criteria = ExplorerTools.BuildCriteria(
                folder, recursive, hrid, null, null, contentType, limit, FolderMatchMode.Lenient);
            (records, SearchPlan plan) = await _workspace.Components.SearchAsync(criteria, cancellationToken);
            resolvedFolder = folder is null ? null : plan.ResolvedFolder?.Describe(folder);
        }
        else
        {
            throw new ArgumentException("Specify components, or folder and/or hrid.");
        }

        if (fix)
        {
            TemplateService.EnsureNoTemplates(records);
        }

        var health = await _workspace.Health.InspectAsync(records, history, cancellationToken);

        // The heaviest cases come first: a footprint that Altium does not see
        // matters more than a parameter without a number, and only the beginning of the list is listed by name.
        var withIssues = health
            .Where(item => item.HasIssues)
            .OrderBy(item => item.Issues.Min(issue => Severity(issue.Kind)))
            .ThenBy(item => item.Record.Hrid, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var fixable = withIssues.Where(item => item.IsFixable).ToList();

        // The component type is assigned by a tag and creates no revisions, so it is separate from the revision edit:
        // a part with no other mismatches gets no revision.
        var revisionFixes = withIssues.Where(item => item.NeedsRevisionFix).ToList();
        var typeFixes = withIssues.Where(item => item.TypeFix is not null).ToList();

        var detailed = PageOf(withIssues, offset, item => new
        {
            component = item.Record.Hrid,
            revision = item.Record.RevisionId,
            folder = item.Record.FolderPath,
            issues = item.Issues.Select(issue => new { kind = issue.Kind, detail = issue.Detail, fixable = issue.Fixable }).ToList(),
        });

        var report = new
        {
            resolvedFolder,
            @checked = records.Count,
            withIssues = withIssues.Count,
            fixable = fixable.Count,
            byKind = withIssues
                .SelectMany(item => item.Issues)
                .GroupBy(issue => issue.Kind)
                .ToDictionary(group => group.Key, group => group.Count()),
            components = detailed.Page,
            nextOffset = detailed.NextOffset,
        };

        if (!fix)
        {
            return report;
        }

        if (fixable.Count == 0)
        {
            return new { report, fix = new { applied = 0, note = "Nothing to fix." } };
        }

        int batchLimit = _workspace.Options.MaxWriteBatch;
        var now = revisionFixes.Take(batchLimit).ToList();
        var later = revisionFixes.Skip(batchLimit).Select(item => item.Record.Hrid).ToList();

        var preview = now
            .Select(item => (object)new
            {
                component = item.Record.Hrid,
                issues = item.Issues.Where(issue => issue.Kind != ComponentHealthService.Kinds.MissingComponentType)
                    .Select(issue => issue.Kind).Distinct().ToList(),
            })
            .Concat(typeFixes.Select(item => (object)$"{item.Record.Hrid}: assign type '{item.TypeFix!.TypePath}' (no new revision)"))
            .ToList();

        if (await gate.DeferIfLargeAsync(
                now.Count + typeFixes.Count,
                now.Concat(typeFixes).Select(item => item.Record.FolderPath).Distinct().ToList(),
                $"Fix for {now.Count} components (format) and {typeFixes.Count} (type)",
                preview,
                cancellationToken) is { } confirmation)
        {
            return confirmation;
        }

        // The type is separate from the revision edit: the tag is assigned by type, one call per type.
        var typeResults = new List<object>();

        foreach (var group in typeFixes.GroupBy(item => item.TypeFix!.TypeGuid!, StringComparer.OrdinalIgnoreCase))
        {
            var itemGuids = group.Select(item => item.Record.ItemGuid).ToList();
            await _workspace.ComponentTypes.AssignAsync(itemGuids, group.Key, cancellationToken);

            typeResults.Add(new
            {
                componentType = group.First().TypeFix!.TypePath,
                components = group.Select(item => item.Record.Hrid).ToList(),
            });
        }

        int totalApplied = typeFixes.Count; // topped up below with the applied revision edits
        var chargeFolders = now.Concat(typeFixes).Select(item => item.Record.FolderPath).Distinct().ToList();

        if (typeFixes.Count > 0)
        {
            await _workspace.Audit.WriteAsync(new AuditEntry
            {
                Operation = CheckOperation,
                Outcome = "done",
                Affected = typeFixes.Count,
                Summary = $"Component type assigned: {typeFixes.Count}",
                After = typeResults,
            }, cancellationToken);
        }

        var edits = now
            .Select(item => new BatchEdit(item.Record.LatestRevision!, item.Record.Hrid, item.FixChange))
            .ToList();

        // Fixing only the type — no revisions are needed at all.
        BatchResult batch = edits.Count == 0
            ? new BatchResult([], [])
            : await _workspace.Batch.ApplyAsync(
                edits, releaseNote, RevisionBatch.DefaultChunkSize, progress: null, cancellationToken);

        var restored = edits.ToDictionary(edit => edit.Hrid, edit => edit.Change.Parameters, StringComparer.OrdinalIgnoreCase);
        totalApplied += batch.Applied.Count;

        PlanChargeReceipt? planReceipt = _workspace.Guard.ChargeIfPlanned(CheckOperation, totalApplied, chargeFolders);

        if (edits.Count > 0)
        {
            await _workspace.Audit.WriteAsync(new AuditEntry
            {
                Operation = CheckOperation,
                Outcome = batch.Failures.Count == 0 ? "done" : "done with failures",
                Affected = batch.Applied.Count,
                Plan = planReceipt?.Number,
                Summary = $"Format fixed for {batch.Applied.Count} components",
                After = batch.Applied.Select(result => new
                {
                    result.Hrid,
                    revision = $"{result.PreviousRevisionId} → {result.NewRevisionId}",
                    restored = restored.GetValueOrDefault(result.Hrid),
                }).ToList(),
            }, cancellationToken);
        }

        return new
        {
            report,
            fix = new
            {
                applied = batch.Applied.Count,
                failed = batch.Failures.Count,
                typeAssigned = typeFixes.Count > 0 ? typeFixes.Count : (int?)null,
                typeAssignments = typeResults.Count > 0 ? typeResults : null,
                results = batch.Applied.Take(ResultsShown).Select(result => new
                {
                    component = result.Hrid,
                    previousRevision = result.PreviousRevisionId,
                    newRevision = result.NewRevisionId,
                    restoredValues = restored.GetValueOrDefault(result.Hrid) is { Count: > 0 } values ? values : null,
                    corrections = result.Corrections,
                }).ToList(),
                resultsOmitted = batch.Applied.Count > ResultsShown ? batch.Applied.Count - ResultsShown : (int?)null,
                failures = batch.Failures.Select(failure => new { component = failure.Hrid, error = failure.Error }).ToList(),
                remaining = later.Count > 0 ? later : null,
                note = later.Count > 0
                    ? $"At most {batchLimit} components are fixed per call; repeat the call for the rest."
                    : null,
                plan = planReceipt?.Describe(),
            },
        };
    }

    [McpServerTool(Name = "vault_cleanup_parameters", Destructive = true, Idempotent = false)]
    [Description("""
        Removes extra parameters from component revisions in bulk by a rule — it does not clear
        the value but deletes the key itself. The rule is one of two: keep — leave only
        the listed parameters, remove everything else; drop — remove only the listed ones.

        Selection: a components list or the folder folder (recursive — with nested ones) and the hrid pattern,
        as in vault_check_components.

        dryRun=true writes nothing and shows how many parts and which parameters
        would go. fix=true releases a new revision without the extra parameters for the affected parts;
        parts with nothing to remove get no revision.
        """)]
    public Task<object> CleanupParametersAsync(
        [Description("Components: identifiers or GUIDs. Empty — select by folder and/or hrid.")]
        string[]? components = null,
        [Description("Folder: path or GUID.")]
        string? folder = null,
        [Description("Include nested folders.")]
        bool recursive = false,
        [Description("Identifier pattern with %, for example CMP-016-%.")]
        string? hrid = null,
        [Description("Content type; default altium-component.")]
        string? contentType = null,
        [Description("Leave only these parameters, remove everything else. Mutually exclusive with drop.")]
        string[]? keep = null,
        [Description("Remove only these parameters, if present. Mutually exclusive with keep.")]
        string[]? drop = null,
        [Description("Remove the found parameters with a new revision.")]
        bool fix = false,
        [Description("Dry run: read everything, write nothing, show the write plan.")]
        bool dryRun = false,
        [Description("Maximum components to check when selecting by folder.")]
        int limit = 2000,
        [Description("""
            From which position to show the detailed list of components that have something to remove (100 per
            call). Take nextOffset from the previous response; the checks and fix do not depend on it.
            """)]
        int offset = 0,
        [Description("Note that goes into the revision release history.")]
        string releaseNote = "Parameter cleanup via MCP",
        [Description("Confirmation token: needed to apply fix for more than 4 components after the preview. The other parameters are not needed then.")]
        string? confirmToken = null,
        CancellationToken cancellationToken = default) =>
        DryRun.RunAsync(
            CleanupOperation,
            dryRun,
            () => _workspace.Guard.RunAsync(
                CleanupOperation,
                confirmToken,
                (gate, ct) => CleanupParametersCoreAsync(
                    components, folder, recursive, hrid, contentType, keep, drop, fix, limit, offset, releaseNote, gate, ct),
                cancellationToken));

    private async Task<object> CleanupParametersCoreAsync(
        string[]? components,
        string? folder,
        bool recursive,
        string? hrid,
        string? contentType,
        string[]? keep,
        string[]? drop,
        bool fix,
        int limit,
        int offset,
        string releaseNote,
        ConfirmationGate gate,
        CancellationToken cancellationToken)
    {
        bool hasKeep = keep is { Length: > 0 };
        bool hasDrop = drop is { Length: > 0 };

        if (hasKeep == hasDrop)
        {
            throw new ArgumentException(
                "Specify exactly one rule: keep (leave only the listed parameters) "
                + "or drop (remove only the listed ones).");
        }

        IReadOnlyList<ComponentRecord> records;

        if (components is { Length: > 0 })
        {
            records = await _workspace.Components.ReadByIdsAsync(components, cancellationToken);

            await _workspace.Components.EnsureFoundAsync(components, records, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(folder) || !string.IsNullOrWhiteSpace(hrid))
        {
            var criteria = ExplorerTools.BuildCriteria(folder, recursive, hrid, null, null, contentType, limit);
            (records, _) = await _workspace.Components.SearchAsync(criteria, cancellationToken);
        }
        else
        {
            throw new ArgumentException("Specify components, or folder and/or hrid.");
        }

        if (fix)
        {
            TemplateService.EnsureNoTemplates(records);
        }

        var keepSet = hasKeep ? new HashSet<string>(keep!, StringComparer.OrdinalIgnoreCase) : null;
        var dropSet = hasDrop ? new HashSet<string>(drop!, StringComparer.OrdinalIgnoreCase) : null;

        var plan = records
            .Select(record => new
            {
                Record = record,
                Names = record.Parameters.Keys
                    .Where(name => keepSet is null ? dropSet!.Contains(name) : !keepSet.Contains(name))
                    .ToList(),
            })
            .Where(item => item.Names.Count > 0)
            .ToList();

        var detailed = PageOf(plan, offset, item => new
        {
            component = item.Record.Hrid,
            folder = item.Record.FolderPath,
            parameters = item.Names,
        });

        var report = new
        {
            @checked = records.Count,
            affected = plan.Count,
            parametersToRemove = plan.Sum(item => item.Names.Count),
            topNames = plan
                .SelectMany(item => item.Names)
                .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .Take(10)
                .Select(group => new { name = group.Key, count = group.Count() })
                .ToList(),
            components = detailed.Page,
            nextOffset = detailed.NextOffset,
        };

        if (!fix)
        {
            return report;
        }

        if (plan.Count == 0)
        {
            return new { report, fix = new { applied = 0, note = "Nothing to remove: no part has the rule's parameters." } };
        }

        int batchLimit = _workspace.Options.MaxWriteBatch;
        var now = plan.Take(batchLimit).ToList();
        var later = plan.Skip(batchLimit).Select(item => item.Record.Hrid).ToList();

        if (await gate.DeferIfLargeAsync(
                now.Count,
                now.Select(item => item.Record.FolderPath).Distinct().ToList(),
                $"Parameter cleanup for {now.Count} components",
                now.Select(item => (object)new { component = item.Record.Hrid, remove = item.Names }).ToList(),
                cancellationToken) is { } confirmation)
        {
            return confirmation;
        }

        var edits = now
            .Select(item => new BatchEdit(
                item.Record.LatestRevision!,
                item.Record.Hrid,
                new ComponentChange { DeleteParameters = new HashSet<string>(item.Names, StringComparer.OrdinalIgnoreCase) }))
            .ToList();

        BatchResult batch = await _workspace.Batch.ApplyAsync(
            edits, releaseNote, RevisionBatch.DefaultChunkSize, progress: null, cancellationToken);

        PlanChargeReceipt? planReceipt = _workspace.Guard.ChargeIfPlanned(
            CleanupOperation, batch.Applied.Count, now.Select(item => item.Record.FolderPath).Distinct().ToList());

        await _workspace.Audit.WriteAsync(new AuditEntry
        {
            Operation = CleanupOperation,
            Outcome = batch.Failures.Count == 0 ? "done" : "done with failures",
            Affected = batch.Applied.Count,
            Plan = planReceipt?.Number,
            Summary = $"Extra parameters removed for {batch.Applied.Count} components",
            After = batch.Applied.Select(result => new
            {
                result.Hrid,
                revision = $"{result.PreviousRevisionId} → {result.NewRevisionId}",
                removed = result.Corrections,
            }).ToList(),
        }, cancellationToken);

        return new
        {
            report,
            fix = new
            {
                applied = batch.Applied.Count,
                failed = batch.Failures.Count,
                results = batch.Applied.Take(ResultsShown).Select(result => new
                {
                    component = result.Hrid,
                    previousRevision = result.PreviousRevisionId,
                    newRevision = result.NewRevisionId,
                    removed = result.Corrections,
                }).ToList(),
                resultsOmitted = batch.Applied.Count > ResultsShown ? batch.Applied.Count - ResultsShown : (int?)null,
                failures = batch.Failures.Select(failure => new { component = failure.Hrid, error = failure.Error }).ToList(),
                remaining = later.Count > 0 ? later : null,
                note = later.Count > 0
                    ? $"At most {batchLimit} components are fixed per call; repeat the call for the rest."
                    : null,
                plan = planReceipt?.Describe(),
            },
        };
    }
}
