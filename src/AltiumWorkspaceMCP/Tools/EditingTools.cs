using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using AltiumWorkspaceMCP.Safety;
using AltiumWorkspaceMCP.Vault;
using ModelContextProtocol.Server;

namespace AltiumWorkspaceMCP.Tools;

/// <summary>Parameter edit of one component.</summary>
public sealed class ParameterUpdate
{
    [JsonPropertyName("component")]
    [Description("Component identifier (CMP-…) or item GUID.")]
    public required string Component { get; init; }

    [JsonPropertyName("parameters")]
    [Description("New parameter values: name → value. Unknown parameters are added.")]
    public Dictionary<string, string> Parameters { get; init; } = [];

    [JsonPropertyName("deleteParameters")]
    [Description("""
        Parameters to remove from the revision entirely (not clear the value but
        delete the key itself). A name cannot also be in parameters — the edit is rejected.
        """)]
    public string[] DeleteParameters { get; init; } = [];

    [JsonPropertyName("description")]
    [Description("New component description; omit to keep the old one.")]
    public string? Description { get; init; }

    [JsonPropertyName("comment")]
    [Description("New component name — the Comment column in Altium; omit to keep the old one.")]
    public string? Comment { get; init; }
}

/// <summary>What to create when copying a component.</summary>
public sealed class CopyRequest
{
    [JsonPropertyName("parameters")]
    [Description("Parameter values in which the copy differs from the sample.")]
    public Dictionary<string, string> Parameters { get; init; } = [];

    [JsonPropertyName("description")]
    [Description("Copy description; omit — as in the sample.")]
    public string? Description { get; init; }

    [JsonPropertyName("comment")]
    [Description("Copy name — the Comment column in Altium; omit — as in the sample.")]
    public string? Comment { get; init; }

    [JsonPropertyName("links")]
    [Description("""
        Links in which the copy differs from the sample, in the same form as in vault_set_links:
        role (symbol, footprint, footprints, template, datasheet) and target; for footprints — a targets list
        (the whole footprint set, the first is primary). The other links are carried over as is.
        """)]
    public LinkRequest[] Links { get; init; } = [];

    [JsonPropertyName("componentType")]
    [Description("""
        Component type of the copy: name, path in the type tree (Passive\Resistors) or GUID.
        Omit — the sample's type.
        """)]
    public string? ComponentType { get; init; }
}

/// <summary>Relinking one model of a component.</summary>
public sealed class LinkRequest
{
    [JsonPropertyName("role")]
    [Description("""
        Role: symbol (symbol), footprint (primary footprint — one target, additional ones are not touched),
        footprints (the whole footprint set — a targets list), template (template), datasheet.
        """)]
    public required string Role { get; init; }

    [JsonPropertyName("target")]
    [Description("""
        Identifier or GUID of the object to reference: a symbol, footprint
        or template. Empty — the link is removed. Not used for the footprints role (see targets).
        """)]
    public string? Target { get; init; }

    [JsonPropertyName("targets")]
    [Description("""
        Only for the footprints role: the whole footprint set as a list of identifiers or GUIDs, the first is
        primary. Old footprints not in the list are removed; an empty list [] removes all.
        For example the package variants Normal / Least / Most: ["PCC-000-0527", "PCC-000-0528", "PCC-0014"].
        """)]
    public string[]? Targets { get; init; }
}

/// <summary>A relink group: these components get these links.</summary>
public sealed class LinkGroup
{
    [JsonPropertyName("components")]
    [Description("Group components: identifiers (CMP-…) or item GUIDs.")]
    public string[] Components { get; init; } = [];

    [JsonPropertyName("links")]
    [Description("Which links to assign to the whole group: role and target. One target per role.")]
    public LinkRequest[] Links { get; init; } = [];
}

/// <summary>
/// Changing vault content: parameters, models, copies, moving and deletion.
/// </summary>
[McpServerToolType]
public sealed class EditingTools
{
    private const string TableWriteOperation = "vault_table_write";
    private const string UpdateOperation = "vault_update_parameters";
    private const string MoveOperation = "vault_move_items";
    private const string DeleteOperation = "vault_delete_items";
    private const string LinksOperation = "vault_set_links";
    private const string CopyOperation = "vault_copy_components";
    private const string RestoreOperation = "vault_restore_from_revision";
    private const string RepairOperation = "vault_repair_links";
    private const string RestoreItemsOperation = "vault_restore_items";

    /// <summary>How many edit results are listed by name; the rest as a count (the response size limit).</summary>
    private const int ResultsShown = 50;

    private const string DryRunDescription =
        "Dry run: read everything, write nothing and show what was read and what would be written.";

    private readonly VaultWorkspace _workspace;

    public EditingTools(VaultWorkspace workspace) => _workspace = workspace;

    [McpServerTool(Name = "vault_table_write", Destructive = true, Idempotent = false)]
    [Description("""
        Writes a component table back into the vault — the second half of the work
        in the Batch Edit style: the table is read by vault_table, the edits are returned by this tool.

        The format matches the vault_table output: columns and rows. The hrid column is required,
        a row is matched to a component by it. The folder and revision columns are
        skipped on write: the folder is changed by vault_move_items, the revision number is assigned by the server.
        An empty cell means an empty parameter value.

        Components where nothing changed are skipped, and no revision is created for them.

        Typed parameters (voltage, current, temperature, capacitance, resistance, etc.)
        are written as Altium shows them: "5.5V", "100µA", "70°C", "100nF", "4.7k".
        The number Altium works with is computed automatically; a row with a value
        that does not parse as a quantity of its type is rejected with an explanation (rejected),
        the other rows are applied.

        If the edit sets a Manufacturer Part Number or LCSC Part# that another part already has
        (including another row of the same call), the response has a duplicates warning
        (the edit is not rejected).

        The response fits the size limit: the summary (applied, skipped, failed) in full, per-part lists — the first ones, the rest as a count resultsOmitted.
        """)]
    public Task<object> WriteTableAsync(
        [Description("Column names — as in the vault_table output.")]
        string[] columns,
        [Description("Value rows; the order and number of cells must match columns.")]
        string?[][] rows,
        [Description("""
            What an empty parameter cell means: clear (default) — write an empty
            value; delete — remove the parameter from the revision entirely.
            """)]
        string emptyMeans = "clear",
        [Description("Note that goes into the revision release history.")]
        string releaseNote = "Table edit via MCP",
        [Description("Confirmation token: needed to apply the bulk edit shown by the preview (above the guarded-mode threshold). The other parameters are not needed then.")]
        string? confirmToken = null,
        [Description(DryRunDescription)]
        bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        DryRun.RunAsync(
            TableWriteOperation,
            dryRun,
            () => _workspace.Guard.RunAsync(
                TableWriteOperation,
                confirmToken,
                (_, ct) => WriteTableCoreAsync(columns, rows, emptyMeans, releaseNote, ct),
                cancellationToken));

    private async Task<object> WriteTableCoreAsync(
        string[] columns,
        string?[][] rows,
        string emptyMeans,
        string releaseNote,
        CancellationToken cancellationToken)
    {
        bool emptyMeansDelete = emptyMeans.Trim().ToLowerInvariant() switch
        {
            "clear" => false,
            "delete" => true,
            _ => throw new ArgumentException(
                $"Unknown emptyMeans value '{emptyMeans}'. Allowed: clear, delete.", nameof(emptyMeans)),
        };

        var edits = ComponentTable.Parse(
            columns, rows.Select(row => (IReadOnlyList<string?>)row).ToList(), emptyMeansDelete);

        var updates = edits.Select(edit => new ParameterUpdate
        {
            Component = edit.Component,
            Parameters = edit.Parameters.ToDictionary(
                entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase),
            DeleteParameters = edit.DeleteParameters.ToArray(),
            Description = edit.Description,
            Comment = edit.Comment,
        }).ToArray();

        return (await PlanAndApplyAsync(updates, releaseNote, TableWriteOperation, cancellationToken)).Response;
    }

    [McpServerTool(Name = "vault_update_parameters", Destructive = true, Idempotent = false)]
    [Description("""
        Pinpoint parameter edit of several components. For large selections the
        vault_table and vault_table_write pair is more convenient.

        Versioning as in Altium: a released revision is immutable, so the
        next one is created — with the previous parameters, the edits made and the links to
        the symbol, footprint and template carried over — and then released.

        If the edit sets a Manufacturer Part Number or LCSC Part# that another part already has
        (including another row of the same call), the response has a duplicates warning
        (the edit is not rejected).

        The response fits the size limit: the summary (applied, skipped, failed) in full, per-part lists — the first ones, the rest as a count resultsOmitted.
        """)]
    public Task<object> UpdateParametersAsync(
        [Description("What to change: one record per component.")]
        ParameterUpdate[] updates,
        [Description("Note that goes into the revision release history.")]
        string releaseNote = "Parameter change via MCP",
        [Description("Confirmation token: needed to apply the bulk edit shown by the preview (above the guarded-mode threshold). The other parameters are not needed then.")]
        string? confirmToken = null,
        [Description(DryRunDescription)]
        bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        DryRun.RunAsync(
            UpdateOperation,
            dryRun,
            () => _workspace.Guard.RunAsync(
                UpdateOperation,
                confirmToken,
                async (_, ct) => (await PlanAndApplyAsync(updates, releaseNote, UpdateOperation, ct)).Response,
                cancellationToken));

    [McpServerTool(Name = "vault_set_links", Destructive = true, Idempotent = false)]
    [Description("""
        Rearrange footprints (symbols, templates) in groups — one call. The target is the identifier
        of a model (PCC-…, SYM-…, CMPT-…) or a revision GUID from vault_components; the active
        revision of the target is taken. An empty target removes the link.

        Groups: assignments = [{components:[…], links:[{role, target}]}, …] — for example "these 120
        resistors to R 0603, these 80 to R 0402" in one call. One group is written shorter:
        components + links. A component cannot be in two groups with the same role.

        Footprints. The footprint role (one target) changes only the primary footprint and leaves the others
        alone. Several footprints on a part (the Normal / Least / Most variants of one package) —
        the footprints role with a targets list: {"role":"footprints","targets":["PCC-000-0527","PCC-000-0528",
        "PCC-0014"]}. The list is the whole set, the first is primary, the others are numbered in order, as in Altium
        ("PCBLIB", "PCBLIB 1", "PCBLIB 2"). Old footprints not in the list are removed; to change the primary —
        reorder the list; to remove all — targets: []. The same set again — the part is skipped (skipped).
        The footprint and footprints roles cannot be combined on one component. An empty target of the footprint role
        with several footprints — a refusal for this part (the other parts of the call go on as usual):
        to remove the primary, give the whole set with the footprints role, to remove all — an empty list.

        Like a parameter edit, this is a content change: a new revision is created (in batches
        of 200), links the edit does not touch are carried over unchanged. Components whose
        link is already like that and is written in the Altium format are skipped (skipped).
        Confirmation is one for the whole call; the preview shows groups, not every part.

        The response fits the size limit: the summary (applied, skipped, failed, groups) in full, per-part lists — the first ones, the rest as a count resultsOmitted.
        """)]
    public Task<object> SetLinksAsync(
        [Description("Components of one group (short form; together with links).")]
        string[]? components = null,
        [Description("Which links to assign to the components of the short form.")]
        LinkRequest[]? links = null,
        [Description("Relink groups: {components, links}. Cannot be combined with components and links.")]
        LinkGroup[]? assignments = null,
        [Description("Note that goes into the revision release history.")]
        string releaseNote = "Model relink via MCP",
        [Description("Confirmation token: needed to apply the bulk edit shown by the preview (above the guarded-mode threshold). The other parameters are not needed then.")]
        string? confirmToken = null,
        [Description(DryRunDescription)]
        bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        DryRun.RunAsync(
            LinksOperation,
            dryRun,
            () => _workspace.Guard.RunAsync(
                LinksOperation,
                confirmToken,
                (gate, ct) => SetLinksCoreAsync(ToGroups(components, links, assignments), releaseNote, gate, ct),
                cancellationToken));

    /// <summary>The short form components+links is one group; otherwise assignments is taken.</summary>
    private static IReadOnlyList<LinkGroupSpec> ToGroups(string[]? components, LinkRequest[]? links, LinkGroup[]? assignments)
    {
        bool shortForm = components is { Length: > 0 } || links is { Length: > 0 };

        if (shortForm && assignments is { Length: > 0 })
        {
            throw new ArgumentException(
                "Specify either components and links (one group), or assignments (several groups), but not both.");
        }

        if (assignments is { Length: > 0 })
        {
            return assignments
                .Select(group => new LinkGroupSpec(
                    group.Components,
                    group.Links.Select(link => new LinkTargetSpec(link.Role, link.Target, link.Targets)).ToList()))
                .ToList();
        }

        return shortForm
            ? [new LinkGroupSpec(
                components ?? [],
                (links ?? []).Select(link => new LinkTargetSpec(link.Role, link.Target, link.Targets)).ToList())]
            : [];
    }

    private async Task<object> SetLinksCoreAsync(
        IReadOnlyList<LinkGroupSpec> requested,
        string releaseNote,
        ConfirmationGate gate,
        CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var groups = LinkGroups.Validate(requested);

        // Every target is read once, however many groups and roles reference it.
        var targets = new Dictionary<string, LinkTarget>(StringComparer.OrdinalIgnoreCase);

        foreach (string target in groups
                     .SelectMany(group => group.Links)
                     .SelectMany(LinkGroups.TargetsOf)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            targets[target] = await _workspace.Components.ResolveLinkTargetInfoAsync(target, cancellationToken);
        }

        var everyone = groups.SelectMany(group => group.Components).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var records = await _workspace.Components.ReadByIdsAsync(everyone, cancellationToken);
        await _workspace.Components.EnsureFoundAsync(everyone, records, cancellationToken);

        // A template in the components list — a refusal; a template as a link target (role: template) — normal work.
        TemplateService.EnsureNoTemplates(records);

        var index = BuildIndex(records);
        string Key(string component) => index[component].ItemGuid.ToUpperInvariant();

        var overlaps = LinkGroups.FindOverlaps(groups, Key);
        if (overlaps.Count > 0)
        {
            throw new InvalidOperationException(LinkGroups.DescribeOverlaps(overlaps));
        }

        // One record per component: the roles of different groups of one component are merged into one edit.
        var wanted = new Dictionary<string, List<LinkAssignment>>(StringComparer.Ordinal);

        foreach (LinkGroupSpec group in groups)
        {
            var assignments = group.Links
                .Select(link => link.Role == LinkRole.Footprints
                    ? new LinkAssignment(
                        link.Role,
                        null,
                        LinkGroups.TargetsOf(link).Select(target => targets[target].RevisionGuid).ToList())
                    : new LinkAssignment(
                        link.Role,
                        string.IsNullOrWhiteSpace(link.Target) ? null : targets[link.Target.Trim()].RevisionGuid))
                .ToList();

            foreach (string component in group.Components)
            {
                string key = Key(component);
                if (!wanted.TryGetValue(key, out var list))
                {
                    wanted[key] = list = [];
                }

                list.AddRange(assignments.Where(assignment => list.All(known => known.Role != assignment.Role)));
            }
        }

        int affected = LinkGroups.CountAffected(groups, Key);

        var groupLines = groups.Select((group, number) =>
            (object)($"Group {number + 1}: {group.Components.Select(Key).Distinct().Count()} parts → "
                + string.Join(", ", group.Links.Select(link => $"{LinkRole.Describe(link.Role)}: " + DescribeTarget(link, targets))))).ToList();

        if (await gate.DeferIfLargeAsync(
                affected,
                records.Select(record => record.FolderPath).Distinct().ToList(),
                $"Relinking links of {affected} components ({groups.Count} groups)",
                groupLines,
                cancellationToken) is { } confirmation)
        {
            return confirmation;
        }

        var currentLinks = await VaultGateway.ReadInChunksAsync(
            records.Select(record => record.RevisionGuid).Where(guid => !string.IsNullOrEmpty(guid)).Select(guid => guid!),
            chunk => _workspace.Gateway.GetItemRevisionLinksAsync(
                VaultFilter.In("ParentItemRevisionGUID", chunk), limit: 100000, cancellationToken: cancellationToken));

        var linksByRevision = currentLinks
            .GroupBy(link => link.ParentItemRevisionGUID ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var edits = new List<BatchEdit>();
        var skipped = new List<object>();
        var failures = new List<object>();

        foreach (ComponentRecord record in records)
        {
            if (!wanted.TryGetValue(record.ItemGuid.ToUpperInvariant(), out var assignments))
            {
                continue;
            }

            if (record.LatestRevision is not { } latest)
            {
                failures.Add(new { component = record.Hrid, error = "the component has no revision that can be changed" });
                continue;
            }

            var existingLinks = linksByRevision.GetValueOrDefault(record.RevisionGuid ?? string.Empty, []);

            // The same link is already in place and written correctly — a new revision would give nothing.
            // A link without footprint data or a vault GUID is still rewritten:
            // that is usually why it is assigned again.
            bool setsFootprints = assignments.Any(assignment => assignment.Role == LinkRole.Footprints);

            try
            {
                if (RevisionService.IsReleased(latest)
                    && !assignments.Any(assignment => ChangesLinks(assignment, existingLinks))
                    && !ComponentHealthService.Inspect(record, existingLinks, []).Any(issue => IsLinkIssue(issue, setsFootprints)))
                {
                    skipped.Add(new { component = record.Hrid, reason = "links already match and are written in the Altium format" });
                    continue;
                }
            }
            catch (FootprintSetException exception)
            {
                // A refusal for this part before writing: the other parts of the call go on as usual.
                failures.Add(new { component = record.Hrid, error = exception.DescribeFor(record.Hrid) });
                continue;
            }

            edits.Add(new BatchEdit(latest, record.Hrid, new ComponentChange { Links = assignments }));
        }

        // As with vault_table_write: a large edit is cut into calls, the remainder is returned explicitly.
        int limit = _workspace.Options.MaxWriteBatch;
        int remaining = Math.Max(0, edits.Count - limit);
        edits = edits.Take(limit).ToList();

        BatchResult batch = await _workspace.Batch.ApplyAsync(
            edits, releaseNote, RevisionBatch.DefaultChunkSize, progress: null, cancellationToken);

        foreach ((string hrid, string error) in batch.Failures)
        {
            failures.Add(new { component = hrid, error });
        }

        string? plan = null;

        if (batch.Applied.Count > 0 || failures.Count > 0)
        {
            plan = await WriteAuditAsync(
                LinksOperation, batch.Applied, failures,
                records.Select(record => record.FolderPath).Distinct().ToList(), cancellationToken);
        }

        return LinksResponse(batch, skipped, failures, groupLines, stopwatch.ElapsedMilliseconds, remaining, limit, plan);
    }

    /// <summary>
    /// Response of <c>vault_set_links</c>: the summary (applied, skipped, failed, groups) and per-part details.
    /// The response size is held by <see cref="ResponseFit"/> — for a dry run and for a real write alike.
    /// </summary>
    internal static object LinksResponse(
        BatchResult batch,
        IReadOnlyList<object> skipped,
        IReadOnlyList<object> failures,
        IReadOnlyList<object> groupLines,
        long elapsedMs,
        int remaining,
        int limit,
        string? plan) => new
        {
            applied = batch.Applied.Count,
            skipped = skipped.Count,
            failed = failures.Count,
            elapsedMs,
            groups = groupLines,
            results = batch.Applied.Take(ResultsShown).Select(result => new
            {
                component = result.Hrid,
                newRevision = result.NewRevisionId,
                changedLinks = result.ChangedLinks,
                corrections = result.Corrections.Count > 0 ? result.Corrections : null,
            }).ToList(),
            resultsOmitted = batch.Applied.Count > ResultsShown ? batch.Applied.Count - ResultsShown : (int?)null,
            failures = failures.Take(ResultsShown).ToList(),
            failuresOmitted = failures.Count > ResultsShown ? failures.Count - ResultsShown : (int?)null,
            skippedComponents = skipped.Count > 0 ? skipped.Take(ResultsShown).ToList() : null,
            remaining = remaining > 0 ? remaining : (int?)null,
            note = remaining > 0
                ? $"At most {limit} components are changed per call. {remaining} left: "
                    + "repeat the same call — the already relinked ones will be skipped."
                : null,
            plan,
        };

    /// <summary>How to name the assignment target in the preview: one target, a footprint set or removal.</summary>
    private static string DescribeTarget(LinkTargetSpec link, IReadOnlyDictionary<string, LinkTarget> targets)
    {
        if (link.Role == LinkRole.Footprints)
        {
            var list = LinkGroups.TargetsOf(link).ToList();

            return list.Count == 0
                ? "(all footprints are removed)"
                : string.Join(", ", list.Select((target, number) =>
                    targets[target].Label + (number == 0 ? " (primary)" : $" (#{number})")));
        }

        return string.IsNullOrWhiteSpace(link.Target) ? "(the link is removed)" : targets[link.Target.Trim()].Label;
    }

    /// <summary>Whether the assignment changes the set of revision links.</summary>
    private static bool ChangesLinks(
        LinkAssignment assignment,
        IReadOnlyList<AltiumWorkspaceMCP.Soap.Vault.ALU_ItemRevisionLink> links)
    {
        string role = LinkRole.Normalize(assignment.Role);

        if (role is LinkRole.Footprint or LinkRole.Footprints)
        {
            return FootprintLinks.Changes(assignment, links);
        }

        var existing = links.FirstOrDefault(link =>
            string.Equals(link.HRID, role, StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrWhiteSpace(assignment.TargetRevisionGuid)
            ? existing is not null
            : existing is null
                || !string.Equals(existing.ChildItemRevisionGUID, assignment.TargetRevisionGuid, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A mismatch for which the links are rewritten by a repeated assignment. A footprint set violation —
    /// only when a set (footprints) is assigned: the single footprint role does not fix the set.
    /// </summary>
    private static bool IsLinkIssue(ComponentIssue issue, bool setsFootprints) =>
        issue.Kind is ComponentHealthService.Kinds.FootprintData
            or ComponentHealthService.Kinds.VaultGuid
            or ComponentHealthService.Kinds.UnknownRole
        || (setsFootprints && issue.Kind == ComponentHealthService.Kinds.FootprintSet);

    [McpServerTool(Name = "vault_copy_components", Destructive = false, Idempotent = false)]
    [Description("""
        Creates copies of a component — the same as "Copy" in the Explorer panel, but a whole series at once.

        The sample sets the layout, symbol, footprint, template, type and links to the technical
        descriptions; the copies list describes how the copies differ. This way one operation creates
        a whole line of values: one record per value with its parameter values.

        Pass all differences of a copy in this same call: comment, description, parameters (including
        Manufacturer Part Number), links, componentType. An edit right after copying creates
        an extra revision. Each copy is created by one atomic operation — one revision, type and
        links at once; the response shows what came out (comment, type, models, differences from the sample).

        Identifiers of the new components are issued by the server by the folder naming scheme.

        Duplicate protection: before creation each copy is checked by Manufacturer Part Number and
        LCSC Part# — including a value inherited from the sample unchanged. If parts with
        the same value are found — duplicates in the response, the copies are not created; allowDuplicates=true creates them
        anyway. The search index is updated with a delay: a part created seconds ago may not
        be found yet, so a match of the same new MPN in two copies of one call
        is caught separately, without a call to the search service.

        The response fits the size limit: the summary (created) in full, per-copy lists — the first ones, the rest as a count componentsOmitted.
        """)]
    public Task<object> CopyComponentsAsync(
        [Description("Sample component: identifier or GUID.")]
        string source,
        [Description("Copies: one record per component to create.")]
        CopyRequest[] copies,
        [Description("Folder for the copies: full path, path ending or GUID (an inexact address is rejected); empty — the sample's folder.")]
        string? targetFolder = null,
        [Description("Note that goes into the revision release history.")]
        string releaseNote = "Component copy via MCP",
        [Description("Create the copies even if a part with such a Manufacturer Part Number or LCSC Part# already exists.")]
        bool allowDuplicates = false,
        [Description("Confirmation token: needed to apply the bulk edit shown by the preview (above the guarded-mode threshold). The other parameters are not needed then.")]
        string? confirmToken = null,
        [Description(DryRunDescription)]
        bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        DryRun.RunAsync(
            CopyOperation,
            dryRun,
            () => _workspace.Guard.RunAsync(
                CopyOperation,
                confirmToken,
                (gate, ct) => CopyComponentsCoreAsync(source, copies, targetFolder, releaseNote, allowDuplicates, gate, ct),
                cancellationToken));

    private async Task<object> CopyComponentsCoreAsync(
        string source,
        CopyRequest[] copies,
        string? targetFolder,
        string releaseNote,
        bool allowDuplicates,
        ConfirmationGate gate,
        CancellationToken cancellationToken)
    {
        if (copies.Length == 0)
        {
            throw new ArgumentException("No copies specified.", nameof(copies));
        }

        var sourceRecords = await _workspace.Components.ReadByIdsAsync([source], cancellationToken);

        if (sourceRecords.Count == 1)
        {
            TemplateService.EnsureNotCopySource(sourceRecords[0]);
        }

        // Duplicates are checked before the confirmation and before the first copy is created; the sample's parameters
        // are inherited by the copy, so an unchanged MPN is the main case.
        long duplicateCheckMs = 0;

        if (!allowDuplicates && sourceRecords.Count == 1)
        {
            var keysByCopy = copies
                .Select(copy => DuplicateFinder.Keys(DuplicateFinder.Effective(sourceRecords[0].Parameters, copy.Parameters)))
                .ToList();

            // Within the call — without a call to the search service: the index does not see values invented
            // by this same call, so a database check would not catch two copies with one new MPN.
            var withinCall = DuplicateFinder.WithinCall(
                keysByCopy.Select((keys, index) => new CallRow((index + 1).ToString(), keys)));

            if (withinCall.Count > 0)
            {
                return new
                {
                    created = 0,
                    duplicates = withinCall.Take(ResultsShown).Select(WithinCallCopyView).ToList(),
                    duplicatesOmitted = withinCall.Count > ResultsShown ? withinCall.Count - ResultsShown : (int?)null,
                    note = "No copies were created: several copies of this call get the same value of "
                        + "Manufacturer Part Number or LCSC Part#. Set different values in the copies' parameters "
                        + "or repeat the call with allowDuplicates=true.",
                };
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var (duplicates, hits) = await FindCopyDuplicatesAsync(keysByCopy, cancellationToken);
            duplicateCheckMs = clock.ElapsedMilliseconds;

            if (duplicates.Count > 0)
            {
                return new
                {
                    created = 0,
                    duplicates = duplicates.Take(ResultsShown).ToList(),
                    duplicatesOmitted = duplicates.Count > ResultsShown ? duplicates.Count - ResultsShown : (int?)null,
                    existingParts = DescribeParts(hits),
                    duplicateCheckMs,
                    note = "No copies were created: a part with such a Manufacturer Part Number or LCSC Part# already exists. "
                        + "Set new values in the copy's parameters or repeat the call with allowDuplicates=true. "
                        + DuplicateFinder.IndexNote,
                };
            }
        }

        if (await gate.DeferIfLargeAsync(
                copies.Length,
                targetFolder is null ? [] : [targetFolder],
                $"Copying {source}: {copies.Length} copies",
                copies.Select((copy, index) => (object)(
                    $"copy {index + 1} of sample {source} into {targetFolder ?? "the sample's folder"}: "
                    + $"description '{copy.Description ?? "as in the sample"}'"
                    + (copy.Parameters.Count > 0
                        ? "; parameters: " + string.Join(", ", copy.Parameters.Select(entry => $"{entry.Key}={entry.Value}"))
                        : string.Empty))).ToList(),
                cancellationToken) is { } confirmation)
        {
            return confirmation;
        }

        var specs = new List<CopySpec>(copies.Length);

        foreach (CopyRequest copy in copies)
        {
            var links = new List<LinkAssignment>(copy.Links.Length);

            foreach (LinkRequest link in copy.Links)
            {
                string role = LinkRole.Parse(link.Role);

                // The same footprint rules as in vault_set_links.
                var spec = new LinkTargetSpec(role, link.Target, link.Targets);
                LinkGroups.ValidateFootprintRole($"Copy {specs.Count + 1}", spec);

                if (spec.Role == LinkRole.Footprints)
                {
                    var guids = new List<string>();

                    foreach (string footprint in LinkGroups.TargetsOf(spec))
                    {
                        guids.Add(await _workspace.Components.ResolveLinkTargetAsync(footprint, cancellationToken));
                    }

                    links.Add(new LinkAssignment(spec.Role, null, guids));
                    continue;
                }

                string? target = string.IsNullOrWhiteSpace(link.Target)
                    ? null
                    : await _workspace.Components.ResolveLinkTargetAsync(link.Target, cancellationToken);

                links.Add(new LinkAssignment(role, target));
            }

            specs.Add(new CopySpec(
                copy.Parameters.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase),
                copy.Description,
                copy.Comment,
                links,
                copy.ComponentType));
        }

        CopyOutcome outcome = await _workspace.Copies.CopyAsync(
            source, targetFolder, specs, releaseNote, cancellationToken);

        var created = outcome.Created;

        PlanChargeReceipt? planReceipt = _workspace.Guard.ChargeIfPlanned(
            CopyOperation, created.Count, created.Select(copy => copy.FolderPath).Distinct().ToList());

        await _workspace.Audit.WriteAsync(new AuditEntry
        {
            Operation = CopyOperation,
            Outcome = outcome.Error is null ? "done" : "done with failure",
            Affected = created.Count,
            Plan = planReceipt?.Number,
            Summary = $"Component copies of {source} created: {created.Count}",
            After = created.Select(copy => copy.Hrid).ToList(),
            Error = outcome.Error,
        }, cancellationToken);

        // Type "none": the part will not get into the Components panel tree — this must be said at once.
        bool withoutType = created.Any(copy => copy.ComponentType?.Source == TypeSource.None);

        return new
        {
            created = created.Count,
            duplicateCheckMs = allowDuplicates ? (long?)null : duplicateCheckMs,
            components = created.Select(copy => new
            {
                hrid = copy.Hrid,
                folder = copy.FolderPath,
                revision = copy.RevisionId,
                comment = copy.Comment,
                description = copy.Description,
                componentType = copy.ComponentType is null
                    ? null
                    : new
                    {
                        name = copy.ComponentType.Path?.Split('\\').Last(),
                        path = copy.ComponentType.Path,
                        source = copy.ComponentType.Source,
                        warnings = copy.ComponentType.Warnings.Count > 0 ? copy.ComponentType.Warnings : null,
                    },
                models = copy.Models,
                changedParameters = copy.ChangedParameters,
            }).ToList(),
            warning = withoutType
                ? "The component type is determined neither explicitly, nor by the copy's template, nor by the folder template, nor by the sample: "
                    + "the part will not be visible in the Components panel tree. Assign the type: "
                    + "vault_component_types action=assign."
                : null,
            failure = outcome.Error is null
                ? null
                : new
                {
                    copyNumber = outcome.FailedAt,
                    error = outcome.Error,
                    note = "The copies before it were created in full, the others were not created. Repeat the call for the remaining ones.",
                },
            plan = planReceipt?.Describe(),
        };
    }

    /// <summary>Duplicates by MPN and LCSC Part# for each copy (the sample's value counts): one search request.</summary>
    private async Task<(IReadOnlyList<object> Duplicates, IReadOnlyList<DuplicateHit> Hits)> FindCopyDuplicatesAsync(
        IReadOnlyList<IReadOnlyList<DuplicateKey>> keysByCopy,
        CancellationToken cancellationToken)
    {
        var hits = await _workspace.Duplicates.FindAsync(keysByCopy.SelectMany(keys => keys), null, cancellationToken);
        var result = new List<object>();

        for (int index = 0; index < keysByCopy.Count; index++)
        {
            foreach (DuplicateKey key in keysByCopy[index])
            {
                DuplicateHit? hit = hits.FirstOrDefault(candidate =>
                    string.Equals(candidate.Parameter, key.Parameter, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(candidate.Value, key.Value, StringComparison.OrdinalIgnoreCase));

                if (hit is not null)
                {
                    result.Add(DuplicateView(index + 1, null, hit));
                }
            }
        }

        return (result, hits);
    }

    /// <summary>The found parts once each: "CMP-… — name — folder" (in duplicates records — identifiers only).</summary>
    private static List<string> DescribeParts(IEnumerable<DuplicateHit> hits) => hits
        .SelectMany(hit => hit.Existing)
        .DistinctBy(part => part.Hrid, StringComparer.OrdinalIgnoreCase)
        .Select(part => $"{part.Hrid} — {part.Comment ?? "no name"} — {part.Folder}")
        .ToList();

    /// <summary>The <c>duplicates</c> record: whose copy or part it is, the parameter, the value and what already exists in the vault.</summary>
    private static object DuplicateView(int? copy, string? component, DuplicateHit hit) => new
    {
        copy,
        component,
        parameter = hit.Parameter,
        value = hit.Value,
        existing = hit.Existing.Select(part => part.Hrid).ToList(),
    };

    /// <summary>The <c>duplicates</c> record for a match inside a copy call: copy numbers, not vault parts.</summary>
    private static object WithinCallCopyView(WithinCallHit hit) => new
    {
        copy = (int?)null,
        component = (string?)null,
        parameter = hit.Parameter,
        value = hit.Value,
        existing = Array.Empty<string>(),
        copies = hit.RowIds.Select(int.Parse).ToList(),
        note = "inside the call: copies " + DescribeRows(hit.RowIds, "#"),
    };

    /// <summary>"#2 and #5" / "CMP-1 and CMP-2" — a list of call rows for the warning text.</summary>
    private static string DescribeRows(IReadOnlyList<string> rowIds, string prefix = "")
    {
        var marked = rowIds.Select(id => prefix + id).ToList();

        return marked.Count == 1
            ? marked[0]
            : string.Join(", ", marked.Take(marked.Count - 1)) + " and " + marked[^1];
    }

    [McpServerTool(Name = "vault_restore_from_revision", Destructive = true, Idempotent = false)]
    [Description("""
        Returns component parameters from an earlier revision — recovery after a
        failed or mistaken edit.

        The values are taken from the given revision and written as a new one, so the history
        is not rewritten: the old revisions stay in place, and the damaged values
        simply stop being active. Without revision the revision
        preceding the current one is taken.

        By default all parameters of that revision are restored. The parameters list
        limits the restore to the listed ones.
        """)]
    public Task<object> RestoreFromRevisionAsync(
        [Description("Components to restore.")]
        string[] components,
        [Description("Source revision number, for example '8'. Empty — the previous revision.")]
        string? revision = null,
        [Description("Which parameters to restore. Empty — all parameters of that revision.")]
        string[]? parameters = null,
        [Description("Note that goes into the revision release history.")]
        string releaseNote = "Parameter restore from an earlier revision",
        [Description("Confirmation token: needed to apply the bulk edit shown by the preview (above the guarded-mode threshold). The other parameters are not needed then.")]
        string? confirmToken = null,
        [Description(DryRunDescription)]
        bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        DryRun.RunAsync(
            RestoreOperation,
            dryRun,
            () => _workspace.Guard.RunAsync(
                RestoreOperation,
                confirmToken,
                (_, ct) => RestoreFromRevisionCoreAsync(components, revision, parameters, releaseNote, ct),
                cancellationToken));

    private async Task<object> RestoreFromRevisionCoreAsync(
        string[] components,
        string? revision,
        string[]? parameters,
        string releaseNote,
        CancellationToken cancellationToken)
    {
        if (components.Length == 0)
        {
            throw new ArgumentException("No components specified.", nameof(components));
        }

        var records = await _workspace.Components.ReadByIdsAsync(components, cancellationToken);
        await _workspace.Components.EnsureFoundAsync(components, records, cancellationToken);

        var wanted = parameters is { Length: > 0 }
            ? new HashSet<string>(parameters, StringComparer.OrdinalIgnoreCase)
            : null;

        var updates = new List<ParameterUpdate>(records.Count);
        var skipped = new List<object>();

        foreach (ComponentRecord record in records)
        {
            var history = await _workspace.Components.GetRevisionsAsync(record.ItemGuid, cancellationToken);

            var source = string.IsNullOrWhiteSpace(revision)
                // The history is sorted from newest to oldest, so the previous one is the second.
                ? history.Skip(1).FirstOrDefault()
                : history.FirstOrDefault(item =>
                    string.Equals(item.RevisionId, revision, StringComparison.OrdinalIgnoreCase));

            if (source is null)
            {
                skipped.Add(new
                {
                    component = record.Hrid,
                    reason = string.IsNullOrWhiteSpace(revision)
                        ? "the component has no previous revision"
                        : $"no revision '{revision}'; available: "
                            + string.Join(", ", history.Select(item => item.RevisionId)),
                });
                continue;
            }

            var previous = ComponentService.ExtractParameters(source);

            var restore = previous
                .Where(entry => wanted is null || wanted.Contains(entry.Key))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);

            if (restore.Count == 0)
            {
                skipped.Add(new { component = record.Hrid, reason = "the source revision has none of the needed parameters" });
                continue;
            }

            updates.Add(new ParameterUpdate
            {
                Component = record.Hrid,
                Parameters = restore,
                Description = source.Description,
                Comment = source.Comment,
            });
        }

        if (updates.Count == 0)
        {
            return new { applied = 0, reason = "nothing to restore", skipped };
        }

        PlanOutcome outcome = await PlanAndApplyAsync(
            updates.ToArray(), releaseNote, RestoreOperation, cancellationToken);

        // The confirmation preview goes to the client as is: the restore has not started yet.
        return outcome.IsConfirmation ? outcome.Response : new { restored = outcome.Response, skipped };
    }

    [McpServerTool(Name = "vault_repair_links", Destructive = true, Idempotent = true)]
    [Description("""
        Returns lost links to the symbol, footprint and template to components,
        restoring them from the latest earlier revision where they were.

        Useful if after a failure the active revision was left without models: in Altium
        such a component looks empty although the models are kept in the history.
        Components with the links in place are skipped.
        """)]
    public Task<object> RepairLinksAsync(
        [Description("Components to check and restore.")]
        string[] components,
        [Description("Note that goes into the revision release history.")]
        string releaseNote = "Model link restore",
        [Description("Confirmation token: needed to apply the bulk edit shown by the preview (above the guarded-mode threshold). The other parameters are not needed then.")]
        string? confirmToken = null,
        [Description(DryRunDescription)]
        bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        DryRun.RunAsync(
            RepairOperation,
            dryRun,
            () => _workspace.Guard.RunAsync(
                RepairOperation,
                confirmToken,
                (gate, ct) => RepairLinksCoreAsync(components, releaseNote, gate, ct),
                cancellationToken));

    private async Task<object> RepairLinksCoreAsync(
        string[] components,
        string releaseNote,
        ConfirmationGate gate,
        CancellationToken cancellationToken)
    {
        if (components.Length == 0)
        {
            throw new ArgumentException("No components specified.", nameof(components));
        }

        var records = await _workspace.Components.ReadByIdsAsync(components, cancellationToken);
        await _workspace.Components.EnsureFoundAsync(components, records, cancellationToken);

        TemplateService.EnsureNoTemplates(records);

        if (await gate.DeferIfLargeAsync(
                records.Count,
                records.Select(record => record.FolderPath).ToList(),
                $"Restoring links of {records.Count} components",
                cancellationToken: cancellationToken) is { } confirmation)
        {
            return confirmation;
        }

        var repaired = new List<object>();
        var intact = new List<string>();
        var hopeless = new List<string>();

        foreach (ComponentRecord record in records)
        {
            var current = await _workspace.Gateway.GetItemRevisionLinksAsync(
                VaultFilter.Equal("ParentItemRevisionGUID", record.RevisionGuid ?? string.Empty),
                cancellationToken: cancellationToken);

            if (current.Count > 0)
            {
                intact.Add(record.Hrid);
                continue;
            }

            // The history is sorted from newest to oldest, so the first earlier
            // revision whose links survived fits.
            var history = await _workspace.Components.GetRevisionsAsync(record.ItemGuid, cancellationToken);

            List<AltiumWorkspaceMCP.Soap.Vault.ALU_ItemRevisionLink>? source = null;
            string? sourceRevision = null;

            foreach (var older in history.Where(item => item.GUID != record.RevisionGuid))
            {
                var links = await _workspace.Gateway.GetItemRevisionLinksAsync(
                    VaultFilter.Equal("ParentItemRevisionGUID", older.GUID),
                    cancellationToken: cancellationToken);

                if (links.Count > 0)
                {
                    source = links;
                    sourceRevision = older.RevisionId;
                    break;
                }
            }

            if (source is null)
            {
                hopeless.Add(record.Hrid);
                continue;
            }

            var restorable = source.Where(link => !string.IsNullOrEmpty(link.ChildItemRevisionGUID)).ToList();

            // Footprints are restored as a set, in the old order ('PCBLIB', 'PCBLIB 1'…): they are not assigned
            // one by one with the role 'PCBLIB n'.
            var footprints = FootprintLinks.Ordered(restorable);

            var assignments = restorable
                .Where(link => !LinkRole.IsFootprint(link.HRID))
                .Select(link => new LinkAssignment(
                    LinkRole.Normalize(link.HRID ?? string.Empty), link.ChildItemRevisionGUID))
                .ToList();

            if (footprints.Count > 0)
            {
                assignments.Add(new LinkAssignment(
                    LinkRole.Footprints, null, footprints.Select(link => link.ChildItemRevisionGUID!).ToList()));
            }

            RevisionChangeResult result = await _workspace.Revisions.ApplyAsync(
                record.LatestRevision!,
                record.Hrid,
                new ComponentChange { Links = assignments },
                releaseNote,
                cancellationToken);

            repaired.Add(new
            {
                component = record.Hrid,
                fromRevision = sourceRevision,
                newRevision = result.NewRevisionId,
                restoredLinks = restorable.Count,
                roles = assignments.Select(assignment => LinkRole.Describe(assignment.Role)).ToList(),
            });
        }

        PlanChargeReceipt? planReceipt = _workspace.Guard.ChargeIfPlanned(
            RepairOperation, repaired.Count, records.Select(record => record.FolderPath).Distinct().ToList());

        await _workspace.Audit.WriteAsync(new AuditEntry
        {
            Operation = RepairOperation,
            Outcome = "done",
            Affected = repaired.Count,
            Plan = planReceipt?.Number,
            Summary = $"Links restored for {repaired.Count} components",
            After = repaired,
        }, cancellationToken);

        return new
        {
            repaired = repaired.Count,
            details = repaired,
            alreadyLinked = intact,
            withoutHistory = hopeless,
            plan = planReceipt?.Describe(),
        };
    }

    /// <summary>How many items are moved per call; the rest goes to <c>remaining</c>.</summary>
    private const int MaxMovesPerCall = 500;

    /// <summary>Move group: items and one target folder.</summary>
    public sealed record MoveGroup(
        [property: Description("Identifiers or GUIDs of the items to move.")]
        string[] Items,
        [property: Description("Destination folder of this group: full path, path ending or GUID.")]
        string TargetFolder);

    [McpServerTool(Name = "vault_move_items", Destructive = true, Idempotent = true)]
    [Description("""
        Moves items to another folder: components, symbols, footprints, templates —
        everything stored in the vault. No revisions are created, the content does not change.

        One target folder — items + targetFolder. Distribution over several folders in one
        call — moves: [{items:[…], targetFolder:"…"}, …]; all folders are resolved before writing,
        an item in two groups — a refusal. Repeating the same call: those already in place — skipped.
        At most 500 items are moved per call, the rest — in remaining.

        Datasheets move along with the parts by default — to the Datasheets folder
        next to them, which is created if needed. A datasheet that is also attached to
        parts outside the moved set stays in place and is listed in the response:
        one datasheet is often shared by several values.
        """)]
    public Task<object> MoveItemsAsync(
        [Description("Identifiers or GUIDs of the items to move (the single-folder form).")]
        string[]? items = null,
        [Description("Destination folder for items: full path, path ending or GUID (an inexact address is rejected).")]
        string? targetFolder = null,
        [Description("Distribution over folders: a list of groups {items, targetFolder}. Not set together with items.")]
        MoveGroup[]? moves = null,
        [Description("Move datasheets after the parts.")]
        bool withDatasheets = true,
        [Description("Confirmation token: needed to apply the bulk edit shown by the preview (above the guarded-mode threshold). The other parameters are not needed then.")]
        string? confirmToken = null,
        [Description(DryRunDescription)]
        bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        DryRun.RunAsync(
            MoveOperation,
            dryRun,
            () => _workspace.Guard.RunAsync(
                MoveOperation,
                confirmToken,
                (gate, ct) => MoveItemsCoreAsync(items, targetFolder, moves, withDatasheets, gate, ct),
                cancellationToken));

    private async Task<object> MoveItemsCoreAsync(
        string[]? items,
        string? targetFolder,
        MoveGroup[]? moves,
        bool withDatasheets,
        ConfirmationGate gate,
        CancellationToken cancellationToken)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        bool legacy = moves is not { Length: > 0 };

        if (!legacy && (items is { Length: > 0 } || !string.IsNullOrWhiteSpace(targetFolder)))
        {
            throw new ArgumentException("Set either moves, or items with targetFolder — not both at once.");
        }

        MoveGroup[] requested = legacy
            ? items is { Length: > 0 } && !string.IsNullOrWhiteSpace(targetFolder)
                ? [new MoveGroup(items, targetFolder)]
                : throw new ArgumentException("Specify items and targetFolder, or moves: [{items, targetFolder}, …].")
            : moves!;

        // All folders are resolved before writing; if not all were found — a refusal with all errors at once.
        var inputs = new List<MoveGroupInput>(requested.Length);
        var unresolved = new List<string>();

        foreach (MoveGroup group in requested)
        {
            if (group.Items is not { Length: > 0 })
            {
                unresolved.Add($"'{group.TargetFolder}': empty items list");
                continue;
            }

            FolderNode target;
            try
            {
                target = await _workspace.Catalog.ResolveFolderAsync(group.TargetFolder ?? string.Empty, cancellationToken);
            }
            catch (InvalidOperationException error)
            {
                unresolved.Add(error.Message);
                continue;
            }
            catch (ArgumentException error)
            {
                unresolved.Add(error.Message);
                continue;
            }

            var records = await _workspace.Components.ReadByIdsAsync(group.Items, cancellationToken);
            await _workspace.Components.EnsureFoundAsync(group.Items, records, cancellationToken);

            inputs.Add(new MoveGroupInput(
                target,
                records.Select(record => new MoveCandidate(record.ItemGuid, record.Hrid, record.FolderGuid, record.FolderPath)).ToList()));
        }

        if (unresolved.Count > 0)
        {
            throw new InvalidOperationException(
                $"Refused before writing, {unresolved.Count} problems: {string.Join(" | ", unresolved.Take(10))}. Nothing was moved.");
        }

        MoveBatchPlan plan = MoveBatchPlanner.Plan(inputs);

        if (plan.Overlaps.Count > 0)
        {
            throw new InvalidOperationException(
                $"Refused before writing: an item is in two groups — {string.Join("; ", plan.Overlaps.Take(20))}. "
                + "Keep each item in one group. Nothing was moved.");
        }

        var skipped = plan.Groups.SelectMany(group => group.AlreadyThere).ToList();

        if (plan.MovingCount == 0)
        {
            return new
            {
                applied = false,
                moved = 0,
                skipped,
                reason = legacy
                    ? $"All the listed items are already in '{plan.Groups[0].Target.Path}'."
                    : "All the listed items already lie in their destination folders.",
                elapsedMs = watch.ElapsedMilliseconds,
            };
        }

        // Call limit: the first items in group order are taken.
        int budget = MaxMovesPerCall;
        var batch = new List<(FolderNode Target, List<MoveCandidate> Moving)>();
        int deferred = 0;

        foreach (MoveGroupPlan group in plan.Groups)
        {
            int take = Math.Min(budget, group.Moving.Count);
            budget -= take;
            deferred += group.Moving.Count - take;

            if (take > 0)
            {
                batch.Add((group.Target, group.Moving.Take(take).ToList()));
            }
        }

        int total = batch.Sum(group => group.Moving.Count);

        // Both the source and destination folders are checked: a move changes both.
        // Datasheets that move along do not count: the moved items are counted.
        var touchedFolders = batch
            .SelectMany(group => group.Moving.Select(item => item.CurrentFolderPath).Append(group.Target.Path))
            .ToList();

        if (await gate.DeferIfLargeAsync(
                total,
                touchedFolders,
                legacy
                    ? $"Moving {total} items to '{batch[0].Target.Path}'"
                    : $"Moving {total} items to {batch.Count} folders",
                batch.SelectMany(group => group.Moving.Select(
                    item => (object)$"{item.Hrid}: {item.CurrentFolderPath} → {group.Target.Path}")).ToList(),
                cancellationToken) is { } confirmation)
        {
            return confirmation;
        }

        // An item record carries its own folder, so all groups go in one call.
        await _workspace.Gateway.MoveItemsAsync(
            batch.SelectMany(group => group.Moving.Select(item => new AltiumWorkspaceMCP.Soap.Vault.ALU_MoveItem
            {
                GUID = item.ItemGuid,
                FolderGUID = group.Target.Guid,
            })).ToList(),
            cancellationToken);

        var failed = new List<string>();
        var results = new List<(FolderNode Target, List<MoveCandidate> Moving, DatasheetMove? Datasheets)>();

        foreach (var (target, moving) in batch)
        {
            DatasheetMove? datasheets = null;

            if (withDatasheets)
            {
                try
                {
                    datasheets = await _workspace.Datasheets.FollowAsync(
                        moving.Select(item => item.ItemGuid).ToList(), target, cancellationToken);
                }
                catch (Exception error) when (error is InvalidOperationException or VaultOperationException)
                {
                    failed.Add($"datasheets for '{target.Path}' were not moved (the parts were moved): {error.Message}");
                }
            }

            results.Add((target, moving, datasheets));
        }

        // Both the source and destination folders are affected: an edit outside the plan roots in
        // either of them still needs the usual confirmation.
        PlanChargeReceipt? planReceipt = _workspace.Guard.ChargeIfPlanned(MoveOperation, total, touchedFolders);

        int datasheetsMoved = results.Sum(result => result.Datasheets?.Moved.Count ?? 0);

        await _workspace.Audit.WriteAsync(new AuditEntry
        {
            Operation = MoveOperation,
            Outcome = "done",
            Affected = total,
            Plan = planReceipt?.Number,
            Summary = (legacy
                    ? $"Items moved to '{batch[0].Target.Path}': {total}"
                    : $"Items moved: {total}, destination folders: {batch.Count}")
                + (datasheetsMoved > 0 ? $", datasheets: {datasheetsMoved}" : string.Empty),
            Before = batch.SelectMany(group => group.Moving.Select(item => new { item.Hrid, folder = item.CurrentFolderPath })).ToList(),
            After = results.Select(result => new { target = result.Target.Path, datasheets = result.Datasheets?.Moved }).ToList(),
        }, cancellationToken);

        object? DatasheetBlock(DatasheetMove? datasheets) => datasheets is null
            ? null
            : new
            {
                moved = datasheets.Moved,
                folder = datasheets.TargetFolder,
                sharedLeftInPlace = datasheets.Shared,
                note = datasheets.Shared.Count > 0
                    ? "The listed datasheets are also attached to other parts and stayed in place."
                    : null,
            };

        return new
        {
            applied = true,
            moved = total,
            target = legacy ? batch[0].Target.Path : null,
            items = legacy ? batch[0].Moving.Select(item => item.Hrid).ToList() : null,
            datasheets = legacy ? DatasheetBlock(results[0].Datasheets) : null,
            groups = legacy
                ? null
                : results.Select(result => new
                {
                    target = result.Target.Path,
                    moved = result.Moving.Count,
                    items = result.Moving.Select(item => item.Hrid).ToList(),
                    datasheets = DatasheetBlock(result.Datasheets),
                }).ToList(),
            skipped,
            failed,
            remaining = deferred,
            note = deferred > 0
                ? $"At most {MaxMovesPerCall} items are moved per call; repeat the same call — the moved ones will be skipped."
                : null,
            elapsedMs = watch.ElapsedMilliseconds,
            plan = planReceipt?.Describe(),
        };
    }

    [McpServerTool(Name = "vault_delete_items", Destructive = true, Idempotent = true)]
    [Description("""
        Moves items to the vault trash. This is reversible: they can be restored
        with vault_restore_items (action=restore) or by means of Altium.

        An item referenced by the active revision of a live part or a template (the default symbol and
        footprint in .cmpt) is never deleted — neither without force nor with
        it: first move the link to another model (vault_set_links,
        vault_template) or move the items themselves (vault_move_items). If usage could not be
        fully checked — also a refusal. force remains only for references from earlier
        revisions.
        """)]
    public Task<object> DeleteItemsAsync(
        [Description("Identifiers or GUIDs of the items to delete.")]
        string[] items,
        [Description("Delete even items in use.")]
        bool force = false,
        [Description("Confirmation token: needed to apply the bulk edit shown by the preview (above the guarded-mode threshold). The other parameters are not needed then.")]
        string? confirmToken = null,
        [Description(DryRunDescription)]
        bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        DryRun.RunAsync(
            DeleteOperation,
            dryRun,
            () => _workspace.Guard.RunAsync(
                DeleteOperation,
                confirmToken,
                (gate, ct) => DeleteItemsCoreAsync(items, force, gate, ct),
                cancellationToken));

    private async Task<object> DeleteItemsCoreAsync(
        string[] items,
        bool force,
        ConfirmationGate gate,
        CancellationToken cancellationToken)
    {
        var records = await _workspace.Components.ReadByIdsAsync(items, cancellationToken);
        await _workspace.Components.EnsureFoundAsync(items, records, cancellationToken);

        // The check always runs, not only with force: protection does not depend on how the server
        // treats "in use". References of live parts and templates would break
        // the links of those parts and leave them without a symbol or footprint.
        var blocked = await _workspace.DeletionGuard.FindBlockingAsync(
            records.Select(record => (record.ItemGuid, record.Hrid)).ToList(), cancellationToken);

        if (blocked.Count > 0)
        {
            throw new InvalidOperationException(
                $"Refused: the deletion would break references from the active revision of a live part or template — "
                + $"{blocked.Count} item(s) are referenced: {DeletionGuard.DescribeDetails(blocked)}. "
                + "force does not lift this protection (it remains for references from earlier revisions); move "
                + "the live parts to another model (vault_set_links), change the template's default model "
                + "or move the items themselves (vault_move_items), then repeat.");
        }

        if (await gate.DeferIfLargeAsync(
                records.Count,
                records.Select(record => record.FolderPath).ToList(),
                $"Moving {records.Count} items to the trash",
                records.Select(record => (object)$"{record.Hrid} ({record.FolderPath}) → trash").ToList(),
                cancellationToken) is { } confirmation)
        {
            return confirmation;
        }

        var results = await _workspace.Gateway.SoftDeleteItemsAsync(
            records.Select(record => record.ItemGuid).ToList(), force, cancellationToken);

        PlanChargeReceipt? planReceipt = _workspace.Guard.ChargeIfPlanned(
            DeleteOperation, records.Count, records.Select(record => record.FolderPath).Distinct().ToList());

        await _workspace.Audit.WriteAsync(new AuditEntry
        {
            Operation = DeleteOperation,
            Outcome = "done",
            Affected = records.Count,
            Plan = planReceipt?.Number,
            Summary = $"Items sent to the trash: {records.Count}",
            Before = DescribeDeletedForAudit(records),
        }, cancellationToken);

        return new
        {
            deleted = records.Count,
            note = "The items were moved to the trash; they can be restored with vault_restore_items.",
            items = records.Select(record => record.Hrid).ToList(),
            serverResults = results.Count,
            plan = planReceipt?.Describe(),
        };
    }

    /// <summary>List of the deleted for the log: identifier and GUID, at most 200.</summary>
    private static List<object> DescribeDeletedForAudit(IReadOnlyList<ComponentRecord> records)
    {
        const int Limit = 200;

        var entries = records.Take(Limit)
            .Select(record => (object)new { record.Hrid, record.ItemGuid, folder = record.FolderPath })
            .ToList();

        if (records.Count > Limit)
        {
            entries.Add(new { note = $"{records.Count - Limit} more objects, not listed" });
        }

        return entries;
    }

    [McpServerTool(Name = "vault_restore_items", Destructive = true, Idempotent = true)]
    [Description("""
        Working with the vault trash (a paged list).

        Actions:
          list    — what is in the trash: folders and items, the path before deletion (restored
                    from the chain of deleted parents), when it was deleted (deletedAt — if the item
                    went to the trash together with a deleted ancestor folder, this is the time of its deletion
                    (deletedWithFolder — its path), otherwise — the last-edit time of the
                    record itself; the server keeps no separate deletion time), and who references
                    the item — live parts and templates separately (the count and the first 3
                    identifiers of each). pathContains narrows by the restored path;
                    deletedAfter/deletedBefore (ISO time, for example "2026-01-15T10:00:00Z") —
                    by deletion time (deletedAt above), which can cut out a window of an accidental mass deletion;
                    contentTypes — by the content type of the item (for example
                    ["altium-symbol","altium-pcb-component"]); onlyUsed — only items that
                    something references. offset/limit — the items page; foldersOffset —
                    the folders page (paged separately from items with the same limit). In the response
                    items come first and get the budget first, folders get the rest: if they did not
                    fit — page through the items, and when itemsNextOffset becomes null, the folders
                    get the whole response (offset=itemsTotal — folders only at once). For a full
                    walk of the trash page items starting with offset=0 and substituting
                    itemsNextOffset while it is not empty (the sum shown over all pages equals
                    itemsTotal), and likewise folders with foldersOffset/foldersNextOffset. The response
                    always fits the size budget — if it cuts a page before
                    limit, the next offsets are recomputed to what was actually shown. timedOut=true —
                    not everything was counted in ~60 s; itemsNextOffset in this response shows
                    where to continue with the same call. Read-only, needs no confirmation.
          restore — return itemGuids and/or folders (folder GUIDs from list; a folder
                    is restored with its contents, RestoreFolderContent=true;
                    nested folders — parents before children).
        """)]
    public Task<object> RestoreItemsAsync(
        [Description("list or restore.")]
        string action = "restore",
        [Description("For restore — GUIDs of the items to restore (from vault_restore_items action=list or vault_audit).")]
        string[]? itemGuids = null,
        [Description("For restore — GUIDs of the folders to restore (from vault_restore_items action=list); the contents return with the folder.")]
        string[]? folders = null,
        [Description("For list — a substring of the restored path.")]
        string? pathContains = null,
        [Description("For list — not earlier than this deletion time (ISO time, inclusive). For example the window of an accidental deletion: \"2026-01-15T10:00:00Z\".")]
        string? deletedAfter = null,
        [Description("For list — not later than this deletion time (ISO time, inclusive).")]
        string? deletedBefore = null,
        [Description("For list — only these content types (HRID, for example [\"altium-symbol\",\"altium-pcb-component\"]).")]
        string[]? contentTypes = null,
        [Description("For list — show only items referenced by active revisions of live parts or by templates.")]
        bool onlyUsed = false,
        [Description("For list — the position among the selected items to start the page from. Continuation — itemsNextOffset of the previous response.")]
        int offset = 0,
        [Description("For list — the position in the folder list to start the page from (folders are paged separately from items). Continuation — foldersNextOffset of the previous response.")]
        int foldersOffset = 0,
        [Description("For list — how many folders and items to show (separately, unless the response budget cuts it).")]
        int limit = 50,
        [Description("Confirmation token: needed to apply the restore shown by the preview (above the guarded-mode threshold). The other parameters are not needed then.")]
        string? confirmToken = null,
        [Description(DryRunDescription)]
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        string normalized = action.Trim().ToLowerInvariant();

        if (normalized == "list")
        {
            return ListTrashAsync(
                pathContains,
                ParseTimeBound(deletedAfter, nameof(deletedAfter)),
                ParseTimeBound(deletedBefore, nameof(deletedBefore)),
                contentTypes,
                onlyUsed,
                offset,
                foldersOffset,
                limit,
                cancellationToken);
        }

        if (normalized != "restore")
        {
            throw new ArgumentException($"Unknown action '{action}'. Allowed: list, restore.", nameof(action));
        }

        return DryRun.RunAsync(
            RestoreItemsOperation,
            dryRun,
            () => _workspace.Guard.RunAsync(
                RestoreItemsOperation,
                confirmToken,
                (gate, ct) => RestoreItemsCoreAsync(itemGuids ?? [], folders ?? [], gate, ct),
                cancellationToken));
    }

    /// <summary>An ISO time from a tool parameter; empty — no bound. The error names the parameter and an example.</summary>
    private static DateTimeOffset? ParseTimeBound(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(
                value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
        {
            return parsed;
        }

        throw new ArgumentException(
            $"{parameterName}='{value}' is not a time in ISO 8601 format, for example \"2026-09-22T19:50:00Z\".",
            parameterName);
    }

    private async Task<object> ListTrashAsync(
        string? pathContains,
        DateTimeOffset? deletedAfter,
        DateTimeOffset? deletedBefore,
        string[]? contentTypes,
        bool onlyUsed,
        int offset,
        int foldersOffset,
        int limit,
        CancellationToken cancellationToken)
    {
        TrashListing listing = await _workspace.Trash.ListAsync(
            pathContains, deletedAfter, deletedBefore, contentTypes, onlyUsed, offset, foldersOffset, limit, cancellationToken);

        return TrashResponse.Build(listing, _workspace.Options.MaxResponseChars);
    }

    private async Task<object> RestoreItemsCoreAsync(
        string[] itemGuids,
        string[] folders,
        ConfirmationGate gate,
        CancellationToken cancellationToken)
    {
        if (itemGuids.Length == 0 && folders.Length == 0)
        {
            throw new ArgumentException("Specify itemGuids and/or folders — what to restore from the trash.");
        }

        IReadOnlyList<FolderToRestore> orderedFolders = await _workspace.Trash.OrderFoldersForRestoreAsync(
            folders, cancellationToken);

        int affected = itemGuids.Length + orderedFolders.Count;

        var preview = orderedFolders.Select(folder => (object)$"folder: {folder.RestoredPath}")
            .Concat(itemGuids.Select(guid => (object)$"item: {guid}"))
            .ToList();

        if (await gate.DeferIfLargeAsync(
                affected,
                [],
                $"Restore from the trash: folders {orderedFolders.Count}, items {itemGuids.Length}",
                preview,
                cancellationToken) is { } confirmation)
        {
            return confirmation;
        }

        var restoredFolders = new List<object>();

        // Parents before children: the server will not restore a nested folder before its parent.
        foreach (FolderToRestore folder in orderedFolders)
        {
            await _workspace.Gateway.RestoreFoldersAsync([folder.Guid], restoreContent: true, cancellationToken);
            restoredFolders.Add(new { guid = folder.Guid, path = folder.RestoredPath });
        }

        if (orderedFolders.Count > 0 && !DryRun.IsActive)
        {
            await _workspace.Catalog.InvalidateAsync(cancellationToken);
        }

        if (itemGuids.Length > 0)
        {
            await _workspace.Gateway.RestoreItemsAsync(itemGuids, cancellationToken);
        }

        PlanChargeReceipt? planReceipt = _workspace.Guard.ChargeIfPlanned(RestoreItemsOperation, affected, []);

        await _workspace.Audit.WriteAsync(new AuditEntry
        {
            Operation = RestoreItemsOperation,
            Outcome = "done",
            Affected = affected,
            Plan = planReceipt?.Number,
            Summary = $"Restored from the trash: folders {orderedFolders.Count}, items {itemGuids.Length}",
            After = new { folders = restoredFolders, items = itemGuids },
        }, cancellationToken);

        return new
        {
            restoredFolders = restoredFolders.Count,
            restoredItems = itemGuids.Length,
            folders = restoredFolders,
            items = itemGuids,
            plan = planReceipt?.Describe(),
        };
    }

    [McpServerTool(Name = "vault_apply_status", ReadOnly = true)]
    [Description("""
        Deferred bulk edits of all write tools awaiting confirmation:
        token, operation, object count, expiry. An edit is confirmed by repeating
        the same tool call with confirmToken. Non-empty only in guarded mode after an
        edit above the threshold (4 objects by default).

        plan — the active plan (vault_plan), if there is one: while it is approved and has not
        expired, a write that fits it goes without a preview and a token at all.
        """)]
    public object ListPending()
    {
        var pending = _workspace.Guard.ListPending();
        WorkPlan? plan = _workspace.Guard.CurrentPlan;

        return new
        {
            mode = _workspace.Guard.Mode.ToString(),
            confirmThreshold = _workspace.Guard.ConfirmThreshold,
            count = pending.Count,
            pending = pending.Select(change => new
            {
                token = change.Token,
                operation = change.Operation,
                affected = change.Affected,
                summary = change.Summary,
                createdAt = change.CreatedAt,
                expiresAt = change.ExpiresAt,
            }).ToList(),
            plan = plan is null
                ? null
                : new
                {
                    number = plan.Number,
                    approved = plan.Approved,
                    expired = plan.IsExpired(DateTimeOffset.Now),
                    summary = plan.Summary,
                    operations = plan.Operations,
                    folders = plan.Folders,
                    applied = plan.Applied,
                    remaining = plan.Remaining,
                    maxObjects = plan.MaxObjects,
                    expiresAt = plan.ExpiresAt,
                },
        };
    }

    // ── Common path for applying edits ──────────────────────────────────────

    /// <summary>
    /// Reconciles the edits with the current state and applies them or defers them
    /// if the mode requires confirmation.
    /// </summary>
    private async Task<PlanOutcome> PlanAndApplyAsync(
        ParameterUpdate[] updates,
        string releaseNote,
        string operation,
        CancellationToken cancellationToken)
    {
        if (updates.Length == 0)
        {
            throw new ArgumentException("No edits passed.", nameof(updates));
        }

        var (effective, unchanged, rejected, folders) = await BuildPlanAsync(updates, cancellationToken);

        // A large edit is cut into calls: the server releases revisions at a constant
        // speed, and two thousand components in one call will not finish before the client's
        // timeout. The remainder is returned explicitly, not lost.
        int limit = _workspace.Options.MaxWriteBatch;
        List<PlannedChange>? deferredTail = null;

        if (effective.Count > limit)
        {
            deferredTail = effective.Skip(limit).ToList();
            effective = effective.Take(limit).ToList();
        }

        if (effective.Count == 0)
        {
            return new PlanOutcome(new
            {
                applied = 0,
                reason = rejected.Count > 0
                    ? "Nothing to apply: the other edits were rejected, their reasons are listed in rejected."
                    : "No edit changes the data: the values already match the current ones.",
                skipped = unchanged,
                rejected,
            });
        }

        var toApply = effective.Select(entry => entry.Update).ToArray();

        // MPN and LCSC Part# duplicates are a warning, not a refusal: editing existing parts is sometimes needed.
        var duplicatesByComponent = await FindUpdateDuplicatesAsync(effective, cancellationToken);
        var duplicates = duplicatesByComponent.SelectMany(entry => entry.Value).ToList();

        PendingChange? deferred = _workspace.Guard.TryDefer(
            operation,
            effective.Count,
            folders,
            $"Edit of {effective.Count} components",
            JsonSerializer.Serialize(toApply),
            cancellation => ApplyAsync(toApply, releaseNote, operation, cancellation, duplicates: duplicates));

        if (deferred is null)
        {
            object result = await ApplyAsync(toApply, releaseNote, operation, cancellationToken, rejected, duplicates, unchanged);

            return new PlanOutcome(deferredTail is null
                ? result
                : new
                {
                    batch = result,
                    remaining = deferredTail.Count,
                    note = $"At most {limit} components are changed per call. "
                        + $"{deferredTail.Count} left: repeat the call for the remaining rows.",
                    remainingComponents = deferredTail.Select(entry => entry.Record.Hrid).ToList(),
                });
        }

        // The confirmation applies exactly the shown edits and checks them against the vault again.
        return new PlanOutcome(
            _workspace.Guard.Confirmation(
                deferred,
                effective.Select(entry => (object)new
                {
                    component = entry.Record.Hrid,
                    changes = entry.Changes,
                    duplicates = duplicatesByComponent.TryGetValue(entry.Record.Hrid, out var own) ? own : null,
                }).ToList()),
            IsConfirmation: true);
    }

    /// <summary>What the common parameter edit path returned: the response to the client and a preview flag.</summary>
    private sealed record PlanOutcome(object Response, bool IsConfirmation = false);

    /// <summary>
    /// Applies the edits in batches.
    /// </summary>
    /// <remarks>
    /// Components are read in one selection, and changes are applied in portions: the links
    /// of a portion are read by one request, the revisions are created by one and released by one
    /// script. Processing two thousand components one by one took about half an hour
    /// and did not fit the time given to the client.
    /// </remarks>
    private async Task<object> ApplyAsync(
        ParameterUpdate[] updates,
        string releaseNote,
        string operation,
        CancellationToken cancellationToken,
        IReadOnlyList<object>? rejected = null,
        IReadOnlyList<object>? duplicates = null,
        IReadOnlyList<object>? unchanged = null)
    {
        var records = await _workspace.Components.ReadByIdsAsync(
            updates.Select(update => update.Component).ToList(), cancellationToken);

        TemplateService.EnsureNoTemplates(records);

        var index = BuildIndex(records);

        var edits = new List<BatchEdit>(updates.Length);
        var failures = new List<object>();

        foreach (ParameterUpdate update in updates)
        {
            if (!index.TryGetValue(update.Component, out ComponentRecord? record))
            {
                failures.Add(new { component = update.Component, error = "component not found" });
                continue;
            }

            if (record.LatestRevision is null)
            {
                failures.Add(new { component = update.Component, error = "the component has no revision" });
                continue;
            }

            edits.Add(new BatchEdit(
                record.LatestRevision,
                record.Hrid,
                new ComponentChange
                {
                    Parameters = update.Parameters,
                    DeleteParameters = new HashSet<string>(update.DeleteParameters, StringComparer.OrdinalIgnoreCase),
                    Description = update.Description,
                    Comment = update.Comment,
                }));
        }

        BatchResult batch = await _workspace.Batch.ApplyAsync(
            edits, releaseNote, RevisionBatch.DefaultChunkSize, progress: null, cancellationToken);

        foreach ((string hrid, string error) in batch.Failures)
        {
            failures.Add(new { component = hrid, error });
        }

        var folders = records.Select(record => record.FolderPath).Distinct().ToList();
        string? plan = await WriteAuditAsync(operation, batch.Applied, failures, folders, cancellationToken);

        return ApplyResponse(batch, failures, rejected, duplicates, unchanged, plan);
    }

    /// <summary>
    /// Response of <c>vault_update_parameters</c> and <c>vault_table_write</c>: the summary (applied, skipped, failed) and per-part
    /// details. The response size is held by <see cref="ResponseFit"/> — for a dry run and for a real write alike.
    /// </summary>
    /// <param name="unchanged">Edits that change nothing (skipped); <see langword="null"/> — not counted.</param>
    internal static object ApplyResponse(
        BatchResult batch,
        IReadOnlyList<object> failures,
        IReadOnlyList<object>? rejected,
        IReadOnlyList<object>? duplicates,
        IReadOnlyList<object>? unchanged,
        string? plan) => new
        {
            applied = batch.Applied.Count,
            skipped = unchanged?.Count,
            failed = failures.Count,
            results = batch.Applied.Take(ResultsShown).Select(result => new
            {
                component = result.Hrid,
                previousRevision = result.PreviousRevisionId,
                newRevision = result.NewRevisionId,
                createdNewRevision = result.CreatedNewRevision,
                carriedModelLinks = result.CarriedLinks,
                corrections = result.Corrections.Count > 0 ? result.Corrections : null,
            }).ToList(),
            resultsOmitted = batch.Applied.Count > ResultsShown ? batch.Applied.Count - ResultsShown : (int?)null,
            failures,
            rejected = rejected is { Count: > 0 } ? rejected : null,
            skippedComponents = unchanged is { Count: > 0 } ? unchanged.Take(ResultsShown).ToList() : null,
            duplicates = duplicates is { Count: > 0 } ? duplicates : null,
            duplicatesNote = duplicates is { Count: > 0 }
                ? "Warning: other parts already have the same Manufacturer Part Number or "
                    + "LCSC Part# value. The edit was applied; if this is a mistake — fix the value. " + DuplicateFinder.IndexNote
                : null,
            plan,
        };

    /// <summary>
    /// For edits that change Manufacturer Part Number or LCSC Part#: which <b>other</b> parts already have such
    /// a value (component → <c>duplicates</c> records). One search request for all edits.
    /// </summary>
    private async Task<Dictionary<string, List<object>>> FindUpdateDuplicatesAsync(
        IReadOnlyList<PlannedChange> planned,
        CancellationToken cancellationToken)
    {
        var keysByComponent = planned.ToDictionary(
            change => change.Record.Hrid,
            change => DuplicateFinder.Keys(change.Update.Parameters
                .Where(entry => change.Changes.ContainsKey(entry.Key))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);

        // Within the call — without a call to the search service: two edits of one call set the same
        // new MPN, and the search index does not see it yet. A warning, not a refusal.
        var withinCall = DuplicateFinder.WithinCall(
            keysByComponent.Select(entry => new CallRow(entry.Key, entry.Value)));

        foreach (WithinCallHit hit in withinCall)
        {
            foreach (string hrid in hit.RowIds)
            {
                var others = hit.RowIds.Where(other => !string.Equals(other, hrid, StringComparison.OrdinalIgnoreCase)).ToList();
                AddDuplicate(result, hrid, WithinCallUpdateView(hit.Parameter, hit.Value, others));
            }
        }

        var hits = await _workspace.Duplicates.FindAsync(keysByComponent.Values.SelectMany(keys => keys), null, cancellationToken);

        foreach ((string hrid, IReadOnlyList<DuplicateKey> keys) in keysByComponent)
        {
            foreach (DuplicateKey key in keys)
            {
                DuplicateHit? hit = hits.FirstOrDefault(candidate =>
                    string.Equals(candidate.Parameter, key.Parameter, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(candidate.Value, key.Value, StringComparison.OrdinalIgnoreCase));

                var others = hit?.Existing.Where(part => !string.Equals(part.Hrid, hrid, StringComparison.OrdinalIgnoreCase)).ToList();

                if (hit is not null && others is { Count: > 0 })
                {
                    AddDuplicate(result, hrid, DuplicateView(null, hrid, hit with { Existing = others }));
                }
            }
        }

        return result;
    }

    /// <summary>The <c>duplicates</c> record for a match inside an edit call: only the identifiers of the other parts of the call.</summary>
    private static object WithinCallUpdateView(string parameter, string value, IReadOnlyList<string> others) => new
    {
        copy = (int?)null,
        component = (string?)null,
        parameter,
        value,
        existing = others,
        note = "inside the call: " + DescribeRows(others),
    };

    private static void AddDuplicate(Dictionary<string, List<object>> result, string hrid, object view)
    {
        if (!result.TryGetValue(hrid, out var list))
        {
            result[hrid] = list = [];
        }

        list.Add(view);
    }

    private sealed record PlannedChange(
        ParameterUpdate Update,
        ComponentRecord Record,
        Dictionary<string, object> Changes);

    /// <summary>
    /// Drops edits that change nothing, rejects values that Altium would not
    /// accept, and collects the list of affected folders.
    /// </summary>
    private async Task<(List<PlannedChange> Effective, List<object> Unchanged, List<object> Rejected, List<string> Folders)>
        BuildPlanAsync(ParameterUpdate[] updates, CancellationToken cancellationToken)
    {
        var records = await _workspace.Components.ReadByIdsAsync(
            updates.Select(update => update.Component).ToList(), cancellationToken);

        // A general edit of a template would release a revision without the .cmpt file (the type and default folder would be lost): a refusal before writing.
        TemplateService.EnsureNoTemplates(records);

        var index = BuildIndex(records);

        // For parameters the component does not have yet, the type comes from same-named ones in the vault.
        var newNames = updates.SelectMany(update =>
            index.TryGetValue(update.Component, out ComponentRecord? owner) && owner.LatestRevision is { } revision
                ? update.Parameters.Keys.Where(name => ParameterTypeResolver.Find(revision, name) is null)
                : []);

        var newTypes = await _workspace.ParameterTypes.ResolveAsync(newNames, cancellationToken);

        var effective = new List<PlannedChange>();
        var unchanged = new List<object>();
        var rejected = new List<object>();

        foreach (ParameterUpdate update in updates)
        {
            if (!index.TryGetValue(update.Component, out ComponentRecord? record))
            {
                throw new InvalidOperationException(
                    await _workspace.Components.DescribeMissingAsync([update.Component], cancellationToken));
            }

            var changes = new Dictionary<string, object>();
            var problems = new List<string>();

            foreach ((string name, string value) in update.Parameters)
            {
                record.Parameters.TryGetValue(name, out string? current);
                if (string.Equals(current, value, StringComparison.Ordinal))
                {
                    continue;
                }

                changes[name] = new { from = current, to = value };

                var existing = record.LatestRevision is null ? null : ParameterTypeResolver.Find(record.LatestRevision, name);
                string? type = existing is null ? newTypes.GetValueOrDefault(name)?.TypeGuid : existing.ParameterTypeGUID;
                string display = ParameterValueCodec.NormalizeDisplay(value, type);

                if (ParameterValueCodec.Explain(name, display, type) is not null)
                {
                    // An example value of the same type hints at the format; it is looked up only on error.
                    var info = (await _workspace.ParameterTypes.ResolveAsync([name], cancellationToken))[name];
                    string? example = string.Equals(info.TypeGuid, type, StringComparison.OrdinalIgnoreCase) ? info.Example : null;

                    problems.Add(ParameterValueCodec.Explain(name, display, type, example)!);
                }
            }

            if (update.DeleteParameters.Length > 0)
            {
                var conflicts = update.DeleteParameters
                    .Where(name => update.Parameters.ContainsKey(name))
                    .ToList();

                if (conflicts.Count > 0)
                {
                    problems.Add(
                        "A parameter cannot be both set and deleted in one edit: "
                        + string.Join(", ", conflicts) + ".");
                }

                foreach (string name in update.DeleteParameters.Except(conflicts, StringComparer.OrdinalIgnoreCase))
                {
                    if (record.Parameters.TryGetValue(name, out string? current))
                    {
                        changes[name] = new { from = current, to = "(deleted)" };
                    }
                }
            }

            if (problems.Count > 0)
            {
                rejected.Add(new { component = record.Hrid, errors = problems });
                continue;
            }

            if (update.Description is not null
                && !string.Equals(update.Description, record.Description, StringComparison.Ordinal))
            {
                changes["description"] = new { from = record.Description, to = update.Description };
            }

            if (update.Comment is not null
                && !string.Equals(update.Comment, record.Comment, StringComparison.Ordinal))
            {
                changes["comment"] = new { from = record.Comment, to = update.Comment };
            }

            bool released = record.LatestRevision is not null && RevisionService.IsReleased(record.LatestRevision);

            if (changes.Count == 0 && released)
            {
                unchanged.Add(new { component = record.Hrid, reason = "values already match" });
                continue;
            }

            if (changes.Count == 0)
            {
                // The values match, but the active revision is not released: work on the
                // component was left unfinished. The edit brings it to release.
                changes["(release)"] = new { from = "revision not released", to = "revision will be released" };
            }

            effective.Add(new PlannedChange(update, record, changes));
        }

        var folders = effective.Select(change => change.Record.FolderPath).Distinct().ToList();
        return (effective, unchanged, rejected, folders);
    }

    /// <summary>A component is available both by identifier and by item GUID.</summary>
    private static Dictionary<string, ComponentRecord> BuildIndex(IReadOnlyList<ComponentRecord> records)
    {
        var index = new Dictionary<string, ComponentRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (ComponentRecord record in records)
        {
            index[record.Hrid] = record;
            index[record.ItemGuid] = record;
        }

        return index;
    }

    /// <summary>Writes the log and charges the volume to the active plan; returns the line for the response.</summary>
    private async Task<string?> WriteAuditAsync(
        string operation,
        IReadOnlyList<RevisionChangeResult> results,
        IReadOnlyList<object> failures,
        IReadOnlyList<string> folders,
        CancellationToken cancellationToken)
    {
        PlanChargeReceipt? receipt = _workspace.Guard.ChargeIfPlanned(operation, results.Count, folders);

        await _workspace.Audit.WriteAsync(new AuditEntry
        {
            Operation = operation,
            Outcome = failures.Count == 0 ? "done" : "done with failures",
            Affected = results.Count,
            Plan = receipt?.Number,
            Summary = $"Components changed: {results.Count}"
                + (failures.Count > 0 ? $", failures: {failures.Count}" : string.Empty),
            After = results.Select(result => new
            {
                result.Hrid,
                revision = $"{result.PreviousRevisionId} → {result.NewRevisionId}",
            }).ToList(),
            Error = failures.Count == 0 ? null : JsonSerializer.Serialize(failures, ResponseJson.Options),
        }, cancellationToken);

        return receipt?.Describe();
    }
}
