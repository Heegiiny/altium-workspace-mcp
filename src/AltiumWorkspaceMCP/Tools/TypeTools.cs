using System.ComponentModel;
using AltiumWorkspaceMCP.Safety;
using AltiumWorkspaceMCP.Vault;
using ModelContextProtocol.Server;

namespace AltiumWorkspaceMCP.Tools;

/// <summary>
/// Component types and templates — the second and third coordinates of a part in Altium.
/// </summary>
[McpServerToolType]
public sealed class TypeTools
{
    private const string TypeOperation = "vault_component_types";
    private const string TemplateOperation = "vault_template";
    private const string TemplateRelinkOperation = ModelFileService.TemplateRelinkOperation;

    /// <summary>How many converted parts are named individually in the response.</summary>
    private const int RelinkedShown = 25;

    /// <summary>What to say after writing a type or a folder template: whom it affects.</summary>
    private const string FutureComponentsNote =
        "The template setting applies to parts created later. For existing parts the type "
        + "is changed by a tag: vault_component_types assign.";

    private readonly VaultWorkspace _workspace;

    public TypeTools(VaultWorkspace workspace) => _workspace = workspace;

    [McpServerTool(Name = "vault_component_types", Destructive = false, Idempotent = false)]
    [Description("""
        Component types — a tree from Preferences → Data Management → Component Types.
        A part is found in the Components panel by its type, so for order in the vault
        it matters as much as the folder.

        Actions:
          list   — the type tree with full paths (default);
          create — create a type; parent sets the parent, empty — the top level;
          rename — rename a type;
          move   — move a type under another parent; an empty-string parent lifts
                   it to the top level;
          delete — delete the type with nested ones IRREVERSIBLY (there is no trash). If the type or
                   nested ones have parts or templates — a refusal with a list: first assign and
                   vault_template set_type. The search index is updated with a delay;
          assign — assign the type to the components from components.

        A type is addressed by name, full path (Passive\\Resistors) or GUID.
        """)]
    public async Task<object> ManageTypesAsync(
        [Description("list, create, rename, move, delete or assign.")]
        string action = "list",
        [Description("Component type: name, full path or GUID. For create — the name of the new type.")]
        string? type = null,
        [Description("Parent type for create and move.")]
        string? parent = null,
        [Description("New name for rename.")]
        string? newName = null,
        [Description("Components for assign.")]
        string[]? components = null,
        [Description("Confirmation token: needed to apply assign for more than 4 components after the preview. The other parameters are not needed then.")]
        string? confirmToken = null,
        [Description("Dry run: read everything, write nothing and show what would be written.")]
        bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        await DryRun.RunAsync(
            TypeOperation,
            dryRun,
            () => _workspace.Guard.RunAsync(
                TypeOperation,
                confirmToken,
                (gate, ct) => ManageTypesCoreAsync(action, type, parent, newName, components, gate, ct),
                cancellationToken));

    private async Task<object> ManageTypesCoreAsync(
        string action,
        string? type,
        string? parent,
        string? newName,
        string[]? components,
        ConfirmationGate gate,
        CancellationToken cancellationToken)
    {
        switch (action.Trim().ToLowerInvariant())
        {
            case "list":
                return await ListTypesAsync(cancellationToken);

            case "create":
                return await CreateTypeAsync(Require(type, nameof(type)), parent, cancellationToken);

            case "rename":
                return await UpdateTypeAsync(
                    Require(type, nameof(type)), Require(newName, nameof(newName)), null, cancellationToken);

            case "move":
                return await UpdateTypeAsync(
                    Require(type, nameof(type)), null, parent ?? string.Empty, cancellationToken);

            case "delete":
                return await DeleteTypeAsync(Require(type, nameof(type)), cancellationToken);

            case "assign":
                return await AssignTypeAsync(
                    Require(type, nameof(type)), components ?? [], gate, cancellationToken);

            default:
                throw new ArgumentException(
                    $"Unknown action '{action}'. Allowed: list, create, rename, move, delete, assign.",
                    nameof(action));
        }
    }

    [McpServerTool(Name = "vault_template", Destructive = true, Idempotent = false)]
    [Description("""
        Editing a component template: its name, description and parameters.

        A template is an ordinary vault item, so a change creates a new revision for it,
        as for a part.

        The component type, folder, default symbol and footprint are in the .cmpt file
        of the released template revision — Altium
        Designer reads them from there. set_type, set_default_folder, set_symbol and set_footprint download the package
        of the active revision, edit only that field in .cmpt and release a new revision with all
        package files; the revision parameter ComponentTypeGuid is also updated by set_type — the
        type choice when creating parts relies on it. If the package has no .cmpt, the action
        refuses: the file is not made up.

        A part's link to a template points to a SPECIFIC REVISION of the template. Every template edit
        (set_type, set_default_folder, set_symbol, set_footprint, set) releases a new revision, and
        the parts stay on the old one — Altium would offer a batch update. The response of these actions
        says how many parts lag behind (laggingComponents) and gives a ready relinkCall string;
        a dry run shows the same number before writing. To move the parts: the relink action or
        relink=all in the call itself. If the parts are edited by a migration anyway, it is MORE EFFICIENT to add
        role:template to the same vault_set_links as the footprint: one part revision instead of
        two. The move does not change the part type — the type is a tag (vault_component_types assign).

        The default symbol and footprint — what the Altium template editor calls
        Symbol/PCB Footprint Model Default Value: a part CREATED from the template
        later gets them. For existing parts the models are changed by vault_set_links. The same caveat —
        a template setting applies to parts created LATER; the type of existing parts
        is changed by a tag: vault_component_types assign.

        Actions:
          show               — template parameters, the component type from the .cmpt file and from the revision
                               parameter (they may differ; Altium reads the file), the folder,
                               default symbol and footprint;
          create             — create a new template: folder sets the template catalog folder,
                               comment — the template name (unique within the folder; description by default),
                               parameters — optional. With basedOn (a sample template) the new
                               template gets the sample's .cmpt file: only ItemGUID,
                               RevisionGUID and what is set in type, defaultFolder, namingTemplate,
                               symbol and footprint are replaced — then Altium sees it in Component Types.
                               Without basedOn there is no .cmpt file, the type and models cannot be set (type,
                               defaultFolder, namingTemplate, symbol and footprint are refused without basedOn);
          set_type           — set the template's component type (type: name, path or GUID);
          set_default_folder — set the template's default folder (folder: path or GUID);
          set_symbol         — set the default symbol (symbol: SYM-… or item GUID;
                               an empty string removes the link); refused if the target is not an altium-symbol;
          set_footprint      — the same for the footprint (footprint: PCC-… or item GUID;
                               the target is altium-pcb-component);
          check              — check the ModelLinks references (default symbol and footprint)
                               against a live item: without template — all templates in one
                               call, with template — only that one. For every problem reference —
                               inTrash (the item is in the trash: HRID, path before deletion, date) with
                               a ready restoreAllInTrash/hint for vault_restore_items, or missing
                               (nowhere) with a hint for set_symbol/set_footprint. Read-only;
                               the response has only the problem reference rows;
          set                — rename (comment — the template name, unique within the folder),
                               describe (description) and/or set parameters (parameters);
                               one of the three is enough. Package files are carried into the new
                               revision; the response has the old and the new value;
          relink             — move parts to the active template revision: components —
                               which ones (empty — all lagging), in batches of 200, at most
                               MaxWriteBatch per call (the rest — remaining, repeat the call);
                               more than 4 parts — preview and confirmToken (or a plan, vault_plan).

        The template name, description and parameters are edited only by this tool: vault_table_write and
        vault_update_parameters reject templates — a general table edit would release a revision
        without the .cmpt file and wipe the type. Moving and deleting a template — vault_move_items and
        vault_delete_items.
        """)]
    public async Task<object> ManageTemplateAsync(
        [Description("show, create, check, set_type, set_default_folder, set_symbol, set_footprint, set or relink. Not needed together with confirmToken.")]
        string action = "show",
        [Description("Template: identifier CMPT-… or GUID. Not given for create; for check empty — all templates.")]
        string? template = null,
        [Description("For create — the template catalog folder; for set_default_folder — the template's default folder: path or GUID.")]
        string? folder = null,
        [Description("For create — the new template's description; for set — the new description (an empty string clears it).")]
        string? description = null,
        [Description("For create and set — the template name (Comment): Altium shows it; unique within the folder. For create if omitted — description is used; for set if omitted — the name is not changed.")]
        string? comment = null,
        [Description("For set_type and create — the component type: name, full path or GUID. For create — only together with basedOn.")]
        string? type = null,
        [Description("For create — the sample template (CMPT-… or GUID): its .cmpt file is taken as is, only ItemGUID, RevisionGUID and what is set in type, defaultFolder, namingTemplate, symbol, footprint are replaced. Without basedOn the template is created without a .cmpt file.")]
        string? basedOn = null,
        [Description("For create with basedOn — the new template's default folder: path or GUID. If omitted — as in the sample.")]
        string? defaultFolder = null,
        [Description("For create with basedOn — the part name template (for example CMP-001-{00000}). If omitted — as in the sample.")]
        string? namingTemplate = null,
        [Description("For set_symbol (required) and create (optional) — the default symbol: SYM-…, item GUID or revision GUID; an empty string removes the link. The target must be an altium-symbol.")]
        string? symbol = null,
        [Description("For set_footprint (required) and create (optional) — the default footprint: PCC-…, item GUID or revision GUID; an empty string removes the link. The target must be an altium-pcb-component.")]
        string? footprint = null,
        [Description("For set and create — template parameters: name → value.")]
        Dictionary<string, string>? parameters = null,
        [Description("For relink — which parts to move to the active template revision (identifiers or GUIDs); empty — all lagging.")]
        string[]? components = null,
        [Description("For set_type, set_default_folder, set_symbol, set_footprint and set: none (default) — the parts stay on the old template revision, all — move them in the same call.")]
        string relink = "none",
        [Description("Note that goes into the revision release history.")]
        string releaseNote = "Component template edit via MCP",
        [Description("relink confirmation token: needed to move more than 4 parts after the preview. The other parameters are not needed then.")]
        string? confirmToken = null,
        [Description("Dry run: read everything, write nothing and show what would be written.")]
        bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        await DryRun.RunAsync(
            TemplateOperation,
            dryRun,
            () => _workspace.Guard.RunAsync(
                TemplateRelinkOperation,
                confirmToken,
                (_, ct) => ManageTemplateCoreAsync(
                    action, template, folder, description, comment, type, basedOn, defaultFolder, namingTemplate,
                    symbol, footprint, parameters, components ?? [], TemplateRelinkPlan.ParseRelink(relink),
                    releaseNote, ct),
                cancellationToken));

    private async Task<object> ManageTemplateCoreAsync(
        string action,
        string? template,
        string? folder,
        string? description,
        string? comment,
        string? type,
        string? basedOn,
        string? defaultFolder,
        string? namingTemplate,
        string? symbol,
        string? footprint,
        Dictionary<string, string>? parameters,
        string[] components,
        bool relinkAll,
        string releaseNote,
        CancellationToken cancellationToken)
    {
        string normalizedAction = action.Trim().ToLowerInvariant();

        if (normalizedAction is "create" or "set"
            && parameters is not null
            && parameters.Keys.Any(name => string.Equals(name, "ComponentTypeGuid", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "The ComponentTypeGuid parameter is not written separately: Altium reads the type from the .cmpt file. "
                + "Set the type with the set_type action — it edits .cmpt and updates this parameter.");
        }

        if (normalizedAction == "create")
        {
            return await CreateTemplateAsync(
                folder, description, comment, type, basedOn, defaultFolder, namingTemplate, symbol, footprint,
                parameters, releaseNote, cancellationToken);
        }

        if (normalizedAction == "check")
        {
            return await CheckTemplateLinksAsync(
                string.IsNullOrWhiteSpace(template) ? null : template.Trim(), cancellationToken);
        }

        ComponentTemplate resolved = await _workspace.Templates.ResolveAsync(
            Require(template, nameof(template)), cancellationToken);

        switch (normalizedAction)
        {
            case "show":
                return await DescribeTemplateAsync(resolved, cancellationToken);

            case "set_type":
            {
                ComponentTypeNode node = await _workspace.ComponentTypes.ResolveAsync(
                    Require(type, nameof(type)), cancellationToken);

                TemplateRelease released = await ReleaseTemplateAsync(
                    resolved,
                    node.Guid,
                    null,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["ComponentTypeGuid"] = node.Guid },
                    releaseNote,
                    $"Template {resolved.Hrid}: type → '{node.Path}'",
                    cancellationToken);

                var types = await _workspace.ComponentTypes.ListAsync(cancellationToken);

                return new
                {
                    template = resolved.Hrid,
                    componentType = node.Path,
                    previousComponentType = TypePath(types, released.PreviousType),
                    file = released.FileName,
                    previousRevision = released.PreviousRevision,
                    newRevision = released.NewRevision,
                    files = released.Files,
                    cmptChange = ChangedFragment(released.CmptBefore, released.CmptAfter),
                    note = FutureComponentsNote,
                    laggingComponents = await FollowReleaseAsync(resolved, released, relinkAll, releaseNote, cancellationToken),
                };
            }

            case "set_default_folder":
            {
                FolderNode target = await _workspace.Catalog.ResolveFolderAsync(
                    Require(folder, nameof(folder)), cancellationToken);

                TemplateRelease released = await ReleaseTemplateAsync(
                    resolved,
                    null,
                    target.Guid,
                    new Dictionary<string, string>(),
                    releaseNote,
                    $"Template {resolved.Hrid}: default folder → '{target.Path}'",
                    cancellationToken);

                var folders = await _workspace.Catalog.GetFoldersAsync(cancellationToken);

                return new
                {
                    template = resolved.Hrid,
                    defaultFolder = target.Path,
                    previousDefaultFolder = released.PreviousDefaultFolder is { } previous
                        ? folders.TryGetValue(previous, out FolderNode? node) ? node.Path : previous
                        : null,
                    file = released.FileName,
                    previousRevision = released.PreviousRevision,
                    newRevision = released.NewRevision,
                    files = released.Files,
                    cmptChange = ChangedFragment(released.CmptBefore, released.CmptAfter),
                    note = FutureComponentsNote,
                    laggingComponents = await FollowReleaseAsync(resolved, released, relinkAll, releaseNote, cancellationToken),
                };
            }

            case "set_symbol":
                return await SetModelLinkAsync(
                    resolved, LinkRole.Symbol, "altium-symbol", "Symbol", symbol, relinkAll, releaseNote, cancellationToken);

            case "set_footprint":
                return await SetModelLinkAsync(
                    resolved, LinkRole.Footprint, "altium-pcb-component", "Footprint", footprint, relinkAll, releaseNote, cancellationToken);

            case "relink":
                return await RelinkActionAsync(resolved, components, releaseNote, cancellationToken);

            case "set":
            {
                if (parameters is not { Count: > 0 } && comment is null && description is null)
                {
                    throw new ArgumentException(
                        "Nothing to change: specify comment (name), description or parameters (template parameters).");
                }

                // A value that is already set does not deserve a revision.
                if (comment is not null && string.Equals(comment.Trim(), resolved.Comment?.Trim(), StringComparison.Ordinal))
                {
                    comment = null;
                }

                if (description is not null && string.Equals(description, resolved.Description, StringComparison.Ordinal))
                {
                    description = null;
                }

                if (parameters is not { Count: > 0 } && comment is null && description is null)
                {
                    return new
                    {
                        template = resolved.Hrid,
                        applied = false,
                        reason = "The name and description are already the same: no revision is released.",
                    };
                }

                var edits = new List<string>();

                if (comment is not null)
                {
                    edits.Add($"name → '{comment.Trim()}'");
                }

                if (description is not null)
                {
                    edits.Add($"description → '{description}'");
                }

                if (parameters is { Count: > 0 })
                {
                    edits.Add($"parameters {string.Join(", ", parameters.Keys)}");
                }

                TemplateRelease released = await ReleaseTemplateAsync(
                    resolved, null, null, parameters ?? new Dictionary<string, string>(), releaseNote,
                    $"Template {resolved.Hrid}: {string.Join("; ", edits)}", cancellationToken, comment, description);

                return new
                {
                    template = resolved.Hrid,
                    previousRevision = released.PreviousRevision,
                    newRevision = released.NewRevision,
                    files = released.Files,
                    comment = comment is null ? null : new { previous = released.PreviousComment, current = comment.Trim() },
                    description = description is null ? null : new { previous = released.PreviousDescription, current = description },
                    parameters,
                    laggingComponents = await FollowReleaseAsync(resolved, released, relinkAll, releaseNote, cancellationToken),
                };
            }

            default:
                throw new ArgumentException(
                    $"Unknown action '{action}'. Allowed: show, create, check, set_type, set_default_folder, "
                    + "set_symbol, set_footprint, set, relink.", nameof(action));
        }
    }

    /// <summary>set_symbol/set_footprint: resolves the target to the item GUID of a model of the needed content type and releases a revision.</summary>
    private async Task<object> SetModelLinkAsync(
        ComponentTemplate template,
        string modelLinkName,
        string expectedContentType,
        string roleLabel,
        string? target,
        bool relinkAll,
        string releaseNote,
        CancellationToken cancellationToken)
    {
        if (target is null)
        {
            throw new ArgumentException($"Specify the {roleLabel.ToLowerInvariant()} (parameter symbol/footprint); an empty string removes the link.");
        }

        string itemGuid = string.Empty;
        ComponentRecord? model = null;

        if (target.Length > 0)
        {
            model = await _workspace.Components.ResolveModelAsync(target, expectedContentType, roleLabel, cancellationToken);
            itemGuid = model.ItemGuid;
        }

        TemplateRelease released = await ReleaseTemplateAsync(
            template,
            null,
            null,
            new Dictionary<string, string>(),
            releaseNote,
            model is null
                ? $"Template {template.Hrid}: default {roleLabel.ToLowerInvariant()} removed"
                : $"Template {template.Hrid}: default {roleLabel.ToLowerInvariant()} → {model.Hrid}",
            cancellationToken,
            symbolItemGuid: string.Equals(modelLinkName, LinkRole.Symbol, StringComparison.OrdinalIgnoreCase) ? itemGuid : null,
            footprintItemGuid: string.Equals(modelLinkName, LinkRole.Footprint, StringComparison.OrdinalIgnoreCase) ? itemGuid : null);

        string? previousItemGuid = string.Equals(modelLinkName, LinkRole.Symbol, StringComparison.OrdinalIgnoreCase)
            ? released.PreviousSymbolItemGuid
            : released.PreviousFootprintItemGuid;

        return new
        {
            template = template.Hrid,
            previous = await DescribeModelAsync(previousItemGuid, cancellationToken),
            current = model is null ? null : new { itemGuid = model.ItemGuid, hrid = model.Hrid, name = model.Comment },
            file = released.FileName,
            previousRevision = released.PreviousRevision,
            newRevision = released.NewRevision,
            files = released.Files,
            cmptChange = ChangedFragment(released.CmptBefore, released.CmptAfter),
            note = FutureComponentsNote,
            laggingComponents = await FollowReleaseAsync(template, released, relinkAll, releaseNote, cancellationToken),
        };
    }

    /// <summary>
    /// After a new template revision is released: relink=all — move the parts in the same call, otherwise — say
    /// how many parts stay on the old revisions and give a ready call string. In a dry run the number is the same
    /// as after the write: the template revision is not created yet, so everyone who references it lags.
    /// </summary>
    private async Task<object> FollowReleaseAsync(
        ComponentTemplate template,
        TemplateRelease released,
        bool relinkAll,
        string releaseNote,
        CancellationToken cancellationToken)
    {
        if (relinkAll)
        {
            return await RelinkTemplateAsync(
                template, released.NewRevisionGuid, released.NewRevision, [], releaseNote, approved: null, cancellationToken);
        }

        var laggards = await _workspace.Models.FindLaggingTemplateUsersAsync(
            template.ItemGuid, released.NewRevisionGuid, cancellationToken);

        return TemplateRelinkPlan.DescribeLaggards(template.Hrid, laggards);
    }

    /// <summary>The relink action: the target is the active template revision, read directly (the search index lags).</summary>
    private async Task<object> RelinkActionAsync(
        ComponentTemplate template,
        string[] components,
        string releaseNote,
        CancellationToken cancellationToken)
    {
        var found = await _workspace.Components.ReadByIdsAsync([template.ItemGuid], cancellationToken);

        AltiumWorkspaceMCP.Soap.Vault.ALU_ItemRevision latest = found.Count == 1
            ? found[0].LatestRevision ?? throw new InvalidOperationException($"Template {template.Hrid} has no revision.")
            : throw new InvalidOperationException($"Template {template.Hrid} not found.");

        return await RelinkTemplateAsync(
            template, latest.GUID, latest.RevisionId ?? string.Empty, components, releaseNote, approved: null, cancellationToken);
    }

    /// <summary>
    /// Moves parts to a template revision: in a batch through <see cref="RevisionBatch"/>, above the guarded-mode threshold —
    /// preview and token (or an approved plan), at most MaxWriteBatch per call, the rest — in <c>remaining</c>.
    /// </summary>
    private async Task<object> RelinkTemplateAsync(
        ComponentTemplate template,
        string targetRevisionGuid,
        string targetRevisionId,
        string[] components,
        string releaseNote,
        int? approved,
        CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        ModelRelink relink = await _workspace.Models.RelinkTemplateUsersAsync(
            template, targetRevisionGuid, targetRevisionId, components, releaseNote, approved, cancellationToken);

        if (relink.Relink.AwaitingConfirmation is { Count: > 0 } awaiting)
        {
            string[] pending = awaiting.ToArray();

            PendingChange change = _workspace.Guard.Defer(
                TemplateRelinkOperation,
                pending.Length,
                $"Moving {pending.Length} parts to {template.Hrid} rev. {targetRevisionId}",
                payload: pending,
                execute: ct => RelinkTemplateAsync(
                    template, targetRevisionGuid, targetRevisionId, pending, releaseNote, pending.Length, ct));

            return _workspace.Guard.Confirmation(
                change,
                pending.Select(hrid => (object)$"{hrid}: move to {template.Hrid} rev. {targetRevisionId}").ToList());
        }

        RelinkResult result = relink.Relink;
        string? plan = null;

        if (result.Relinked.Count > 0 || result.Failures.Count > 0)
        {
            PlanChargeReceipt? receipt = _workspace.Guard.ChargeIfPlanned(
                TemplateRelinkOperation, result.Relinked.Count, result.Folders ?? []);

            await _workspace.Audit.WriteAsync(new AuditEntry
            {
                Operation = TemplateRelinkOperation,
                Outcome = result.Failures.Count == 0 ? "done" : "done with failures",
                Affected = result.Relinked.Count,
                Plan = receipt?.Number,
                Summary = $"Parts moved to {template.Hrid} rev. {targetRevisionId}: {result.Relinked.Count}"
                    + (result.Failures.Count > 0 ? $", failures: {result.Failures.Count}" : string.Empty),
                After = result.Relinked.Take(200).Select(item => $"{item.Hrid}: {item.PreviousRevisionId} → {item.NewRevisionId}").ToList(),
            }, cancellationToken);

            plan = receipt?.Describe();
        }

        int remaining = TemplateRelinkPlan.Remaining(result.StillOnOlderRevisions, components);

        return new
        {
            template = template.Hrid,
            revision = targetRevisionId,
            applied = result.Relinked.Count,
            failed = result.Failures.Count,
            elapsedMs = stopwatch.ElapsedMilliseconds,
            relinked = result.Relinked.Take(RelinkedShown).Select(item => new
            {
                component = item.Hrid,
                previousRevision = item.PreviousRevisionId,
                newRevision = item.NewRevisionId,
                changedLinks = item.ChangedLinks,
            }).ToList(),
            relinkedOmitted = result.Relinked.Count > RelinkedShown ? result.Relinked.Count - RelinkedShown : (int?)null,
            failures = result.Failures.Count > 0
                ? result.Failures.Take(RelinkedShown).Select(failure => new { component = failure.Hrid, error = failure.Error }).ToList()
                : null,
            remaining = remaining > 0 ? remaining : (int?)null,
            note = remaining > 0
                ? $"{remaining} parts are left on the old template revisions — repeat the same call: "
                    + "the ones already moved will be skipped."
                : null,
            notes = relink.Notes.Count > 0 ? relink.Notes : null,
            plan,
        };
    }

    /// <summary>
    /// Identifier and name of a model by the GUID of its item — for defaultSymbol/defaultFootprint (show) and
    /// the old value of set_symbol/set_footprint. If not found among the live ones — one trash check:
    /// instead of a raw GUID the hrid says where the item is ("in trash: …", "not in vault: …").
    /// </summary>
    private async Task<object?> DescribeModelAsync(string? itemGuid, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(itemGuid))
        {
            return null;
        }

        var found = await _workspace.Components.ReadByIdsAsync([itemGuid], cancellationToken);
        ComponentRecord? record = found.Count == 1 ? found[0] : null;

        if (record is not null)
        {
            return new { itemGuid, hrid = (string?)record.Hrid, name = (string?)record.Comment, status = "ok" };
        }

        var trash = await _workspace.Trash.FindItemsAsync([itemGuid], cancellationToken);

        if (trash.TryGetValue(itemGuid, out TrashItemLocation? location))
        {
            return new
            {
                itemGuid,
                hrid = ModelLinkLabels.InTrash(location.Hrid, location.DeletedAt),
                name = (string?)null,
                status = "inTrash",
                restoredPath = location.RestoredPath,
                deletedAt = location.DeletedAt,
            };
        }

        return new { itemGuid, hrid = ModelLinkLabels.Missing(itemGuid), name = (string?)null, status = "missing" };
    }

    /// <summary>
    /// The check action: the status of each ModelLinks reference of one template or of all at once —
    /// ok (alive), inTrash (in the trash, with a ready list for vault_restore_items) or missing (nowhere,
    /// with a hint for set_symbol/set_footprint). The response has only the problem rows.
    /// </summary>
    private async Task<object> CheckTemplateLinksAsync(string? template, CancellationToken cancellationToken)
    {
        TemplateLinkCheckSummary summary = await _workspace.TemplateLinkChecks.CheckAsync(template, cancellationToken);

        var trashGuids = summary.Problems
            .Where(problem => problem.Status == ModelLinkStatus.InTrash)
            .Select(problem => problem.ModelItemGuid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Response size limit: rows of problem references only, but there can be many of them
        // over the whole vault — the remainder is shown as a count, as in vault_check_components.
        var budget = new ResponseBudget(_workspace.Options.MaxResponseChars, reserved: 2000);
        var shown = budget.TakeFitting(summary.Problems.Select(problem => (object)new
        {
            template = problem.TemplateHrid,
            role = problem.Role,
            status = problem.Status == ModelLinkStatus.InTrash ? "inTrash" : "missing",
            label = ModelLinkLabels.Format(problem.Status, problem.ModelItemGuid, problem.Hrid, problem.DeletedAt),
            itemGuid = problem.ModelItemGuid,
            deletedAt = problem.DeletedAt,
            restoredPath = problem.RestoredPath,
            hint = problem.Status == ModelLinkStatus.InTrash
                ? $"vault_restore_items action=restore itemGuids=[\"{problem.ModelItemGuid}\"]"
                : $"vault_template {(problem.LinkName == LinkRole.Symbol ? "set_symbol" : "set_footprint")} "
                    + $"template={problem.TemplateHrid} {(problem.LinkName == LinkRole.Symbol ? "symbol" : "footprint")}=<new item>",
        }));

        return new
        {
            templatesChecked = summary.TemplatesChecked,
            linksChecked = summary.LinksChecked,
            templatesWithProblems = summary.TemplatesWithProblems,
            problems = shown,
            problemsOmitted = summary.Problems.Count > shown.Count ? summary.Problems.Count - shown.Count : (int?)null,
            restoreAllInTrash = trashGuids.Count > 0
                ? "vault_restore_items action=restore itemGuids=[" + string.Join(", ", trashGuids.Select(guid => $"\"{guid}\"")) + "]"
                : null,
            note = summary.Problems.Count == 0 ? "All ModelLinks references are fine." : null,
        };
    }

    private async Task<object> CreateTemplateAsync(
        string? folder,
        string? description,
        string? comment,
        string? type,
        string? basedOn,
        string? defaultFolder,
        string? namingTemplate,
        string? symbol,
        string? footprint,
        Dictionary<string, string>? parameters,
        string releaseNote,
        CancellationToken cancellationToken)
    {
        string folderPath = Require(folder, nameof(folder));

        _workspace.Guard.EnsureAllowed(TemplateOperation, 1, [folderPath]);

        // The .cmpt file is taken from the sample: it must not be made up. Without a sample there is no file, and the type,
        // default folder, name template, symbol and footprint are stored exactly in it. A refusal —
        // before creating, so as not to leave a template that the agent thinks has the setting assigned.
        if (string.IsNullOrWhiteSpace(basedOn)
            && (!string.IsNullOrWhiteSpace(type) || !string.IsNullOrWhiteSpace(defaultFolder)
                || !string.IsNullOrWhiteSpace(namingTemplate) || !string.IsNullOrWhiteSpace(symbol)
                || !string.IsNullOrWhiteSpace(footprint)))
        {
            throw new InvalidOperationException(
                "The type, default folder, name template, symbol and footprint are stored in the .cmpt file, and a "
                + "template without a sample has none. Specify basedOn — an existing template whose .cmpt to take as the basis "
                + "(vault_templates gives the list).");
        }

        TemplateSample? sample = null;

        if (!string.IsNullOrWhiteSpace(basedOn))
        {
            string? typeGuid = string.IsNullOrWhiteSpace(type)
                ? null
                : (await _workspace.ComponentTypes.ResolveAsync(type, cancellationToken)).Guid;

            string? symbolItemGuid = string.IsNullOrWhiteSpace(symbol)
                ? null
                : (await _workspace.Components.ResolveModelAsync(symbol, "altium-symbol", "Symbol", cancellationToken)).ItemGuid;

            string? footprintItemGuid = string.IsNullOrWhiteSpace(footprint)
                ? null
                : (await _workspace.Components.ResolveModelAsync(footprint, "altium-pcb-component", "Footprint", cancellationToken)).ItemGuid;

            string? defaultFolderGuid = string.IsNullOrWhiteSpace(defaultFolder)
                ? null
                : (await _workspace.Catalog.ResolveFolderAsync(defaultFolder, cancellationToken)).Guid;

            sample = new TemplateSample(
                basedOn.Trim(),
                typeGuid,
                defaultFolderGuid,
                string.IsNullOrWhiteSpace(namingTemplate) ? null : namingTemplate.Trim(),
                symbolItemGuid,
                footprintItemGuid);
        }

        // ComponentTypeGuid passed along with the first revision of a new item is silently
        // erased by the server on release (checked empirically), so it is not written here.
        TemplateCreation result = await _workspace.Templates.CreateAsync(
            folderPath, description, comment, parameters, releaseNote, sample, cancellationToken);

        ComponentTemplate created = result.Template;

        await AuditAsync(
            "create", 1,
            result.BasedOn is null
                ? $"Component template '{created.Hrid}' created without a .cmpt file"
                : $"Component template '{created.Hrid}' created from sample {result.BasedOn}",
            cancellationToken);

        return new
        {
            created = true,
            hrid = created.Hrid,
            folder = created.FolderPath,
            revision = created.RevisionId,
            basedOn = result.BasedOn,
            file = result.FileName,
            componentType = sample?.TypeGuid is { } guid
                ? TypePath(await _workspace.ComponentTypes.ListAsync(cancellationToken), guid)
                : null,
            skippedSampleFiles = result.SkippedFiles.Count > 0 ? result.SkippedFiles : null,
            cmptBytes = result.CmptAfter is null ? (int?)null : System.Text.Encoding.UTF8.GetByteCount(result.CmptAfter),
            parameters = created.Parameters,
            warning = result.BasedOn is null
                ? "The template was created without a .cmpt file: its type, default folder, symbol and footprint cannot "
                    + "be set, and Altium will not show it in Component Types. Create the template with basedOn — "
                    + "an existing sample template."
                : null,
            note = result.BasedOn is null
                ? null
                : "The .cmpt file was taken from the sample; only ItemGUID, RevisionGUID and the requested type, folder, "
                    + "name template, symbol and default footprint were replaced (unset ones stayed as in the sample). "
                    + FutureComponentsNote,
        };
    }

    private async Task<object> ListTypesAsync(CancellationToken cancellationToken)
    {
        var types = await _workspace.ComponentTypes.ListAsync(cancellationToken);
        var templates = await _workspace.Templates.ListAsync(cancellationToken);

        var templatesByType = templates
            .Where(item => !string.IsNullOrEmpty(item.ComponentTypeGuid))
            .GroupBy(item => item.ComponentTypeGuid!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.Hrid).ToList(),
                StringComparer.OrdinalIgnoreCase);

        return new
        {
            count = types.Count,
            componentTypes = types.Select(node => new
            {
                path = node.Path,
                name = node.Name,
                guid = node.Guid,
                templates = templatesByType.GetValueOrDefault(node.Guid, []),
            }).ToList(),
        };
    }

    private async Task<object> CreateTypeAsync(string name, string? parent, CancellationToken cancellationToken)
    {
        _workspace.Guard.EnsureAllowed(TypeOperation, 1, []);

        ComponentTypeNode created = await _workspace.ComponentTypes.CreateAsync(name, parent, cancellationToken);
        await AuditAsync("create", 1, $"Component type '{created.Path}' created", cancellationToken);

        return new { created = true, path = created.Path, guid = created.Guid };
    }

    private async Task<object> UpdateTypeAsync(
        string type,
        string? newName,
        string? newParent,
        CancellationToken cancellationToken)
    {
        _workspace.Guard.EnsureAllowed(TypeOperation, 1, []);

        ComponentTypeNode updated = await _workspace.ComponentTypes.UpdateAsync(
            type, newName, newParent, cancellationToken);

        await AuditAsync("update", 1, $"The component type is now '{updated.Path}'", cancellationToken);

        return new { updated = true, path = updated.Path, guid = updated.Guid };
    }

    private async Task<object> DeleteTypeAsync(string type, CancellationToken cancellationToken)
    {
        ComponentTypeNode root = await _workspace.ComponentTypes.ResolveAsync(type, cancellationToken);
        var subtree = ComponentTypeUsage.Subtree(await _workspace.ComponentTypes.ListAsync(cancellationToken), root);

        _workspace.Guard.EnsureAllowed(TypeOperation, subtree.Count, []);

        // A type is deleted irreversibly (no trash), so the check always runs and without exceptions.
        var detailCounts = await _workspace.Panel.GetTypeCountsAsync(cancellationToken);
        var templatesByType = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (ComponentTemplate template in await _workspace.Templates.ListAsync(cancellationToken))
        {
            TemplateType templateType = await _workspace.Templates.ReadTypeAsync(template, cancellationToken);

            if (templateType.TypeGuid is { Length: > 0 } typeGuid
                && subtree.Any(node => string.Equals(node.Guid, typeGuid, StringComparison.OrdinalIgnoreCase)))
            {
                if (!templatesByType.TryGetValue(typeGuid, out List<string>? list))
                {
                    templatesByType[typeGuid] = list = [];
                }

                list.Add(template.Hrid);
            }
        }

        var usage = ComponentTypeUsage.Find(
            subtree,
            detailCounts,
            templatesByType.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value, StringComparer.OrdinalIgnoreCase));

        if (usage.Count > 0)
        {
            throw new InvalidOperationException(ComponentTypeUsage.DescribeRefusal(root.Path, usage));
        }

        var removed = await _workspace.ComponentTypes.DeleteAsync(type, cancellationToken);
        await AuditAsync("delete", removed.Count, $"Component types deleted: {string.Join(", ", removed)}",
            cancellationToken);

        return new
        {
            deleted = removed.Count,
            types = removed,
            note = "Type deletion is irreversible: types have no trash. The deleted types had no parts or templates "
                + "(per the search index, which is updated with a delay).",
        };
    }

    private async Task<object> AssignTypeAsync(
        string type,
        string[] components,
        ConfirmationGate gate,
        CancellationToken cancellationToken)
    {
        if (components.Length == 0)
        {
            throw new ArgumentException("No components specified.", nameof(components));
        }

        var records = await _workspace.Components.ReadByIdsAsync(components, cancellationToken);

        await _workspace.Components.EnsureFoundAsync(components, records, cancellationToken);

        ComponentTypeNode node = await _workspace.ComponentTypes.ResolveAsync(type, cancellationToken);

        if (await gate.DeferIfLargeAsync(
                records.Count,
                records.Select(record => record.FolderPath).ToList(),
                $"Assigning type '{node.Path}' to {records.Count} components",
                records.Select(record => (object)$"{record.Hrid}: type → {node.Path}").ToList(),
                cancellationToken) is { } confirmation)
        {
            return confirmation;
        }

        await _workspace.ComponentTypes.AssignAsync(
            records.Select(record => record.ItemGuid).ToList(), type, cancellationToken);

        PlanChargeReceipt? planReceipt = _workspace.Guard.ChargeIfPlanned(
            TypeOperation, records.Count, records.Select(record => record.FolderPath).Distinct().ToList());

        await AuditAsync(
            "assign", records.Count,
            $"Type '{node.Path}' assigned to {records.Count} components", cancellationToken, planReceipt?.Number);

        return new
        {
            assigned = records.Count,
            componentType = node.Path,
            components = records.Select(record => record.Hrid).ToList(),
            note = "Type assignment creates no revisions: the type is stored separately from the part content.",
            plan = planReceipt?.Describe(),
        };
    }

    private async Task<object> DescribeTemplateAsync(
        ComponentTemplate template,
        CancellationToken cancellationToken)
    {
        var types = await _workspace.ComponentTypes.ListAsync(cancellationToken);

        string? PathOf(string? guid) => guid is null
            ? null
            : types.FirstOrDefault(candidate => string.Equals(candidate.Guid, guid, StringComparison.OrdinalIgnoreCase))?.Path ?? guid;

        // Altium reads the type from the .cmpt file of the revision; the revision parameter is a fallback source.
        TemplateType type = await _workspace.Templates.ReadTypeAsync(template, cancellationToken);
        TemplateSettings? settings = await _workspace.Templates.ReadSettingsAsync(template.RevisionGuid, cancellationToken);

        string? defaultFolder = null;
        if (settings?.DefaultFolderGuid is { } folderGuid)
        {
            var folders = await _workspace.Catalog.GetFoldersAsync(cancellationToken);
            defaultFolder = folders.TryGetValue(folderGuid, out FolderNode? folderNode) ? folderNode.Path : folderGuid;
        }

        return new
        {
            hrid = template.Hrid,
            folder = template.FolderPath,
            revision = template.RevisionId,
            comment = template.Comment,
            description = template.Description,
            componentType = PathOf(type.TypeGuid),
            componentTypeGuid = type.TypeGuid,
            componentTypeFromFile = PathOf(type.FromFile),
            componentTypeFromParameter = PathOf(type.FromParameter),
            defaultFolder,
            defaultSymbol = await DescribeModelAsync(settings?.DefaultSymbolItemGuid, cancellationToken),
            defaultFootprint = await DescribeModelAsync(settings?.DefaultFootprintItemGuid, cancellationToken),
            warning = type.Disagree
                ? "The type in the .cmpt file and in the ComponentTypeGuid parameter differ. Altium reads the file, so "
                    + "componentType is from the file; the revision parameter is stale or was written bypassing Altium."
                : null,
            parameters = template.Parameters,
        };
    }

    /// <summary>
    /// Releases a new revision for a template: edits .cmpt, parameters, name and/or description, carries over all package files,
    /// writes to the audit log. A dry run reaches the last step and sends nothing.
    /// </summary>
    private async Task<TemplateRelease> ReleaseTemplateAsync(
        ComponentTemplate template,
        string? typeGuid,
        string? defaultFolderGuid,
        IReadOnlyDictionary<string, string> parameters,
        string releaseNote,
        string auditSummary,
        CancellationToken cancellationToken,
        string? comment = null,
        string? description = null,
        string? symbolItemGuid = null,
        string? footprintItemGuid = null)
    {
        _workspace.Guard.EnsureAllowed(TemplateOperation, 1, [template.FolderPath]);

        TemplateRelease released = await _workspace.Templates.ReleaseEditedAsync(
            template, typeGuid, defaultFolderGuid, symbolItemGuid, footprintItemGuid, parameters, comment,
            description, releaseNote, cancellationToken);

        await AuditAsync(
            "template", 1,
            $"{auditSummary}: revision {released.PreviousRevision} → {released.NewRevision}",
            cancellationToken);

        return released;
    }

    private static string? TypePath(IReadOnlyList<ComponentTypeNode> types, string? guid) =>
        guid is null
            ? null
            : types.FirstOrDefault(candidate => string.Equals(candidate.Guid, guid, StringComparison.OrdinalIgnoreCase))?.Path ?? guid;

    /// <summary>
    /// A fragment of the .cmpt file around the edit — before and after: the whole file (5–6 thousand characters) is not
    /// needed in the response, and the owner must see exactly what changed before checking in Altium.
    /// </summary>
    private static object? ChangedFragment(string? before, string? after)
    {
        if (before is null || after is null)
        {
            return null;
        }

        const int context = 90;
        int first = 0;

        while (first < before.Length && first < after.Length && before[first] == after[first])
        {
            first++;
        }

        if (first == before.Length && first == after.Length)
        {
            return new { changed = false };
        }

        int tail = 0;

        while (tail < before.Length - first && tail < after.Length - first
            && before[^(tail + 1)] == after[^(tail + 1)])
        {
            tail++;
        }

        int start = Math.Max(0, first - context);

        string Window(string text)
        {
            int end = Math.Min(text.Length, text.Length - tail + context);
            return (start > 0 ? "…" : string.Empty) + text[start..end] + (end < text.Length ? "…" : string.Empty);
        }

        return new { changed = true, before = Window(before), after = Window(after) };
    }

    private static string Require(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"This action requires {name}.", name)
            : value;

    private Task AuditAsync(
        string action, int affected, string summary, CancellationToken cancellationToken, int? plan = null) =>
        _workspace.Audit.WriteAsync(new AuditEntry
        {
            Operation = $"{TypeOperation}:{action}",
            Outcome = "done",
            Affected = affected,
            Plan = plan,
            Summary = summary,
        }, cancellationToken);
}
