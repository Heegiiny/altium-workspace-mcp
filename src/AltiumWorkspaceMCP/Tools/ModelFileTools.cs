using System.ComponentModel;
using AltiumWorkspaceMCP.Safety;
using AltiumWorkspaceMCP.Vault;
using ModelContextProtocol.Server;

namespace AltiumWorkspaceMCP.Tools;

/// <summary>
/// Symbol and footprint files — a bridge to altium-designer-mcp, which reads and edits
/// the .SchLib and .PcbLib libraries.
/// </summary>
[McpServerToolType]
public sealed class ModelFileTools
{
    private const string Operation = "vault_model_files";
    private const string CreateOperation = "vault_model_files:create";
    private const string DefaultNote = "Model edit via MCP";

    private readonly VaultWorkspace _workspace;

    public ModelFileTools(VaultWorkspace workspace) => _workspace = workspace;

    [McpServerTool(Name = "vault_model_files", Destructive = true, Idempotent = false)]
    [Description("""
        Symbol (.SchLib) and footprint (.PcbLib) files: export for editing in
        altium-designer-mcp and upload of the edited file back into the vault.

        Actions:
          download — export the object's models to the exchange directory. For a component (CMP-…) —
                     the symbol and footprint linked to it (roles narrows the choice:
                     symbol, footprint), for a model itself — its active revision. In the response
                     agentPath is the path to pass as filepath to the
                     altium-designer-mcp tools (list_components, get_component, update_pad and others);
          list     — what is in the exchange directory and which files were edited after the export;
          upload   — release the edited file as a new revision of the same model; file — the path from
                     download or list. relink — which components to move to the new revision:
                     source (default) — the component the model was exported from;
                     all — all components on the old revisions of the model; none — none.
                     If the model got a new revision after the export, the upload is refused:
                     export again or pass force=true;
          create   — make a NEW model from a file: file (or files[] — several) — .PcbLib
                     (footprint) or .SchLib (symbol), folder — the folder, name — the model name
                     (comment; empty — the file name without extension; with several files — the file
                     name), description, component — link the model to a part at once
                     (one part revision); footprintMode — how to link the footprint:
                     replace (default) — the new one becomes primary instead of the old one,
                     add — the new one is added as an additional one, with the last number (package
                     variants Normal / Least / Most). Writes only new items, does not touch existing
                     ones: a model with the same name in the folder — a refusal (a new revision is given by upload).
                     The identifier is issued by the server by the folder naming scheme; without a scheme — a refusal.
                     One file — one model; it has no preview images;
          relink   — move components to the active revision of the model item without uploading
                     a file; components — which ones (empty — all on old revisions).

        A component link points to a specific model revision: without a move the component
        keeps using the old one. An uploaded revision has no preview images —
        only Altium draws them. dryRun shows what would be released and moved.
        """)]
    public async Task<object> ManageAsync(
        [Description("download, list, upload or relink.")]
        string action = "download",
        [Description("For download — a component or a model; for relink — a model: identifier or GUID.")]
        string? item = null,
        [Description("For download of a component — which models to export: symbol, footprint. Empty — all.")]
        string[]? roles = null,
        [Description("For upload — the edited file: agentPath or path from the download or list response.")]
        string? file = null,
        [Description("For upload — which components to move to the new revision: source, all or none.")]
        string relink = "source",
        [Description("For relink — which components to move. Empty — all on old revisions of the model.")]
        string[]? components = null,
        [Description("For create — several .PcbLib/.SchLib files in one call (together with file or instead of it).")]
        string[]? files = null,
        [Description("For create — the new model's folder: path or GUID, for example Components\\Models\\Footprints.")]
        string? folder = null,
        [Description("For create — the model name (revision comment); empty — the file name without extension. Only with one file.")]
        string? name = null,
        [Description("For create — the model description.")]
        string? description = null,
        [Description("For create — the part to link the model to at once with the footprint/symbol role (by the file's role).")]
        string? component = null,
        [Description("For create with component — the footprint: replace (default, the new one becomes primary) or add (the new one is added as an additional one, with the last number). Does not affect the symbol.")]
        string? footprintMode = null,
        [Description("For upload — release the file even if the model changed after the export.")]
        bool force = false,
        [Description("Note that goes into the revision release history.")]
        string releaseNote = DefaultNote,
        [Description("Confirmation token: needed for relink of more than 4 components and for create of more than 4 files after the preview (issued by relink, upload and create). The other parameters are not needed then.")]
        string? confirmToken = null,
        [Description("Dry run of create, upload and relink: read everything, write nothing and show what would be written.")]
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        switch (action.Trim().ToLowerInvariant())
        {
            case "download":
                return await DownloadAsync(
                    item ?? throw new ArgumentException("For download specify item — a component or a model.", nameof(item)),
                    roles ?? [],
                    cancellationToken);

            case "list":
                return await ListAsync(cancellationToken);

            case "upload":
            {
                if (string.IsNullOrWhiteSpace(file))
                {
                    throw new ArgumentException(
                        "For upload specify file — the path to the edited file from the download or list response.", nameof(file));
                }

                RelinkScope scope = ParseScope(relink);

                return await DryRun.RunAsync(
                    $"{Operation}:upload",
                    dryRun,
                    () => UploadAsync(file, scope, force, releaseNote, cancellationToken));
            }

            case "create":
            {
                var specs = ModelCreateSpec.Build(file, files, name, description);
                FootprintMode mode = FootprintModes.Parse(footprintMode);

                if (mode == FootprintMode.Add && string.IsNullOrWhiteSpace(component))
                {
                    throw new ArgumentException(
                        "footprintMode=add works only together with component: there is nowhere to add the footprint. "
                        + "Specify the part (component) or remove footprintMode.", nameof(footprintMode));
                }

                return await DryRun.RunAsync(
                    CreateOperation,
                    dryRun,
                    () => _workspace.Guard.RunAsync(
                        CreateOperation,
                        confirmToken,
                        (gate, ct) => CreateAsync(
                            gate,
                            specs,
                            folder ?? throw new ArgumentException(
                                "For create specify folder — the new model's folder (for example Components\\Models\\Footprints).", nameof(folder)),
                            component,
                            mode,
                            releaseNote == DefaultNote ? "New model from a file via MCP" : releaseNote,
                            ct),
                        cancellationToken));
            }

            case "relink":
            {
                if (string.IsNullOrWhiteSpace(item))
                {
                    throw new ArgumentException("For relink specify item — a symbol or a footprint.", nameof(item));
                }

                return await DryRun.RunAsync(
                    ModelFileService.RelinkOperation,
                    dryRun,
                    () => _workspace.Guard.RunAsync(
                        ModelFileService.RelinkOperation,
                        confirmToken,
                        (_, ct) => RelinkAsync(item, components ?? [], releaseNote, approved: null, ct),
                        cancellationToken));
            }

            default:
                throw new ArgumentException(
                    $"Unknown action '{action}'. Allowed: download, list, upload, create, relink.", nameof(action));
        }
    }

    private async Task<object> CreateAsync(
        ConfirmationGate gate,
        IReadOnlyList<ModelCreateSpec> specs,
        string folder,
        string? component,
        FootprintMode footprintMode,
        string releaseNote,
        CancellationToken cancellationToken)
    {
        ModelCreatePlan plan = await _workspace.Models.PlanCreateAsync(specs, folder, component, cancellationToken);

        var lines = plan.Entries
            .Select(entry => (object)$"{LinkRole.Describe(entry.Role)} '{entry.Name}' from {entry.FileName} ({entry.Size} bytes) → {plan.Folder.Path}")
            .ToList();

        if (await gate.DeferIfLargeAsync(
                plan.Entries.Count,
                [plan.Folder.Path],
                $"Creating {plan.Entries.Count} new models in '{plan.Folder.Path}'",
                lines,
                cancellationToken) is { } confirmation)
        {
            return confirmation;
        }

        ModelCreateOutcome outcome = await _workspace.Models.ApplyCreateAsync(plan, releaseNote, cancellationToken);

        RevisionChangeResult? linked = null;
        string? linkFailure = null;

        if (plan.Component is not null && outcome.Failure is null && !DryRun.IsActive)
        {
            try
            {
                linked = await _workspace.Models.LinkCreatedAsync(
                    plan.Component, outcome.Created, releaseNote, cancellationToken, footprintMode);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                linkFailure = exception.Message;
            }
        }

        PlanChargeReceipt? receipt = _workspace.Guard.ChargeIfPlanned(CreateOperation, outcome.Created.Count, [plan.Folder.Path]);

        if (outcome.Created.Count > 0)
        {
            await _workspace.Audit.WriteAsync(new AuditEntry
            {
                Operation = CreateOperation,
                Outcome = outcome.Failure is null && linkFailure is null ? "done" : "done with failures",
                Affected = outcome.Created.Count,
                Plan = receipt?.Number,
                Summary = $"New models in '{plan.Folder.Path}': "
                    + string.Join(", ", outcome.Created.Select(model => $"{model.Hrid} «{model.Name}»"))
                    + (linked is null ? string.Empty : $"; linked to {linked.Hrid} (rev. {linked.NewRevisionId})"),
                After = outcome.Created.Select(model => $"{model.Hrid} rev. 1: {model.Name} ← {model.FileName}").ToList(),
            }, cancellationToken);
        }

        return new
        {
            folder = plan.Folder.Path,
            namingScheme = plan.NamingScheme,
            created = outcome.Created.Select(model => new
            {
                hrid = model.Hrid,
                role = LinkRole.Describe(model.Role),
                name = model.Name,
                revision = "1",
                revisionGuid = model.RevisionGuid,
                file = model.FileName,
                size = model.Size,
                serviceFieldsFrom = model.SampleHrid,
            }).ToList(),
            linkedTo = linked is null
                ? null
                : new { component = linked.Hrid, previousRevision = linked.PreviousRevisionId, newRevision = linked.NewRevisionId },
            wouldLinkTo = plan.Component is not null && DryRun.IsActive ? plan.Component.Hrid : null,
            footprintMode = plan.Component is not null && plan.Entries.Any(entry => entry.Role == LinkRole.Footprint)
                ? (footprintMode == FootprintMode.Add ? "add: as an additional footprint" : "replace: as primary instead of the old one")
                : null,
            failure = outcome.Failure is null
                ? null
                : $"Created {outcome.Created.Count} of {plan.Entries.Count}: {outcome.Failure}. The created models remain in the vault; "
                    + "repeat create only for the remaining files.",
            linkFailure = linkFailure is null
                ? null
                : $"The models were created, but part {plan.Component!.Hrid} is not linked: {linkFailure}. Link them with vault_set_links by the identifiers from created.",
            plan = receipt?.Describe(),
            notes = plan.Notes.Count > 0 ? plan.Notes : null,
            note = "The new model has no preview images — only Altium draws them. The model is released as the first revision; "
                + "parts get it after linking (component, vault_set_links).",
        };
    }

    private async Task<object> DownloadAsync(string item, string[] roles, CancellationToken cancellationToken)
    {
        ModelDownload download = await _workspace.Models.DownloadAsync(item, roles, cancellationToken);

        return new
        {
            source = download.Source,
            exchangeRoot = _workspace.Exchange.Root,
            agentExchangeRoot = _workspace.Exchange.AgentRoot,
            files = download.Files.Select(downloaded => new
            {
                role = LinkRole.Describe(downloaded.Role),
                item = downloaded.ItemHrid,
                revision = downloaded.RevisionId,
                latestRevision = downloaded.LatestRevisionId,
                contentType = downloaded.ContentType,
                path = downloaded.Path,
                agentPath = downloaded.AgentPath ?? downloaded.Path,
                size = downloaded.Size,
            }).ToList(),
            notes = download.Notes.Count > 0 ? download.Notes : null,
        };
    }

    private async Task<object> ListAsync(CancellationToken cancellationToken)
    {
        var entries = await _workspace.Models.ListAsync(cancellationToken);

        return new
        {
            exchangeRoot = _workspace.Exchange.Root,
            agentExchangeRoot = _workspace.Exchange.AgentRoot,
            count = entries.Count,
            entries = entries.Select(entry => new
            {
                item = entry.Item,
                revision = entry.Revision,
                role = entry.Role is null ? null : LinkRole.Describe(entry.Role),
                linkedFrom = entry.LinkedFrom,
                downloadedAt = entry.DownloadedAt,
                files = entry.Files.Select(listed => new
                {
                    path = listed.Path,
                    agentPath = listed.AgentPath ?? listed.Path,
                    size = listed.Size,
                    modifiedAfterDownload = listed.ModifiedAfterDownload,
                }).ToList(),
            }).ToList(),
        };
    }

    private async Task<object> UploadAsync(
        string file,
        RelinkScope scope,
        bool force,
        string releaseNote,
        CancellationToken cancellationToken)
    {
        ModelUpload upload = await _workspace.Models.UploadAsync(file, scope, force, releaseNote, cancellationToken);

        await _workspace.Audit.WriteAsync(new AuditEntry
        {
            Operation = $"{Operation}:upload",
            Outcome = upload.Relink.Failures.Count == 0 ? "done" : "done with failures",
            Affected = 1 + upload.Relink.Relinked.Count,
            Summary = $"{upload.Item}: rev. {upload.PreviousRevision} → {upload.NewRevision} from {upload.FileName}; "
                + $"components moved: {upload.Relink.Relinked.Count}",
            After = new
            {
                upload.Item,
                upload.NewRevision,
                upload.NewRevisionGuid,
                relinked = upload.Relink.Relinked.Select(result => $"{result.Hrid}: {result.PreviousRevisionId} → {result.NewRevisionId}").ToList(),
            },
        }, cancellationToken);

        return new
        {
            item = upload.Item,
            previousRevision = upload.PreviousRevision,
            newRevision = upload.NewRevision,
            file = upload.FileName,
            size = upload.Size,
            relink = upload.Relink.AwaitingConfirmation is { Count: > 0 } awaiting
                ? DeferRelink(upload.Item, awaiting, releaseNote)
                : DescribeRelink(upload.Relink),
            notes = upload.Notes.Count > 0 ? upload.Notes : null,
        };
    }

    private async Task<object> RelinkAsync(
        string item,
        string[] components,
        string releaseNote,
        int? approved,
        CancellationToken cancellationToken)
    {
        ModelRelink relink = await _workspace.Models.RelinkAsync(
            item, components, releaseNote, approved, cancellationToken);

        // The move above the threshold was not performed: a preview and a token instead of the result.
        if (relink.Relink.AwaitingConfirmation is { Count: > 0 } awaiting)
        {
            return DeferRelink(relink.Item, awaiting, releaseNote);
        }

        if (relink.Relink.Relinked.Count > 0 || relink.Relink.Failures.Count > 0)
        {
            await _workspace.Audit.WriteAsync(new AuditEntry
            {
                Operation = $"{Operation}:relink",
                Outcome = relink.Relink.Failures.Count == 0 ? "done" : "done with failures",
                Affected = relink.Relink.Relinked.Count,
                Summary = $"Components moved to {relink.Item} rev. {relink.Revision}: {relink.Relink.Relinked.Count}",
                After = relink.Relink.Relinked.Select(result => $"{result.Hrid}: {result.PreviousRevisionId} → {result.NewRevisionId}").ToList(),
            }, cancellationToken);
        }

        return new
        {
            item = relink.Item,
            revision = relink.Revision,
            relink = DescribeRelink(relink.Relink),
            notes = relink.Notes.Count > 0 ? relink.Notes : null,
        };
    }

    /// <summary>Defers moving components to a model: the token will apply it for these same components.</summary>
    private object DeferRelink(string item, IReadOnlyList<string> awaiting, string releaseNote)
    {
        string[] components = awaiting.ToArray();

        PendingChange change = _workspace.Guard.Defer(
            ModelFileService.RelinkOperation,
            components.Length,
            $"Moving {components.Length} components to the active revision {item}",
            payload: components,
            execute: ct => RelinkAsync(item, components, releaseNote, approved: components.Length, ct));

        return _workspace.Guard.Confirmation(
            change,
            components.Select(hrid => (object)$"{hrid}: move to the active revision {item}").ToList());
    }

    private static object DescribeRelink(RelinkResult relink) => new
    {
        relinked = relink.Relinked.Select(result => new
        {
            component = result.Hrid,
            previousRevision = result.PreviousRevisionId,
            newRevision = result.NewRevisionId,
            changedLinks = result.ChangedLinks,
            corrections = result.Corrections.Count > 0 ? result.Corrections : null,
        }).ToList(),
        failures = relink.Failures.Count > 0
            ? relink.Failures.Select(failure => new { component = failure.Hrid, error = failure.Error }).ToList()
            : null,
        stillOnOlderRevisions = relink.StillOnOlderRevisions.Count > 0 ? relink.StillOnOlderRevisions : null,
        hint = relink.StillOnOlderRevisions.Count > 0
            ? "These components reference old revisions of the model. To move them: vault_model_files relink with the item of this model."
            : null,
    };

    private static RelinkScope ParseScope(string relink) => relink.Trim().ToLowerInvariant() switch
    {
        "source" or "" => RelinkScope.Source,
        "all" => RelinkScope.All,
        "none" => RelinkScope.None,
        _ => throw new ArgumentException($"relink accepts source, all or none, got '{relink}'.", nameof(relink)),
    };
}
