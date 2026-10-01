using System.ComponentModel;
using AltiumWorkspaceMCP.Safety;
using AltiumWorkspaceMCP.Vault;
using ModelContextProtocol.Server;

namespace AltiumWorkspaceMCP.Tools;

/// <summary>Operations on the vault folder structure.</summary>
[McpServerToolType]
public sealed class FolderTools
{
    private const string Operation = "vault_folder";

    private readonly VaultWorkspace _workspace;

    public FolderTools(VaultWorkspace workspace) => _workspace = workspace;

    [McpServerTool(Name = "vault_folder", Destructive = true, Idempotent = false)]
    [Description("""
        Changes the vault folder structure. No component revisions are created.

        Actions:
          create       — create a folder: folder gives the name, parent — where to create it;
                         namingScheme gives the naming scheme of the folder's items.
                         As a batch — paths[]: full paths (or relative to parent);
                         missing parents are created before children, existing ones are
                         skipped (skipped), name conflicts — a refusal before writing;

          rename       — rename a folder and/or change its description;
          move         — move a folder with all its contents under another parent;
          delete       — move the folder and nested ones to the trash;
          set_template — assign a default component template to a folder: parts
                         created in it later get this template and with it
                         the component type. Existing parts are not changed —
                         use vault_set_links for them;
          repair_system — bring the Datasheets system folders to the form Altium creates
                         them in (special folder type, hidden in Explorer, naming scheme):
                         folder — one folder; empty — all differing ones in the whole vault.
                         Without dryRun it shows nothing in advance — do a dry run first.
        """)]
    public async Task<object> ManageAsync(
        [Description("create, rename, move, delete, set_template or repair_system.")]
        string action,
        [Description("Folder: full path or GUID. For create — the name of the folder being created. For repair_system empty — all Datasheets folders.")]
        string folder = "",
        [Description("For create and move — the parent folder: path or GUID.")]
        string? parent = null,
        [Description("For rename — the new folder name.")]
        string? newName = null,
        [Description("Folder description for create and rename.")]
        string? description = null,
        [Description("For set_template — the component template: CMPT-… identifier or GUID.")]
        string? template = null,
        [Description("For create — the folder type; empty — inherited from the parent.")]
        string? folderType = null,
        [Description("""
            For create — naming scheme of the items created in the folder, for example
            "CMP-016-{00000}"; empty — do not set.
            """)]
        string? namingScheme = null,
        [Description("For delete — delete even used contents.")]
        bool force = false,
        [Description("For create as a batch — full paths of the folders to create (with parent — relative to it); folder is then not needed. Parents are created before children, existing ones are skipped.")]
        string[]? paths = null,
        [Description("Confirmation token: needed to apply repair_system for more than 4 folders after the preview. The other parameters are not needed then.")]
        string? confirmToken = null,
        [Description("Dry run: read everything, write nothing and show what would be written.")]
        bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        await DryRun.RunAsync(
            Operation,
            dryRun,
            () => _workspace.Guard.RunAsync(
                Operation,
                confirmToken,
                (gate, ct) => ManageCoreAsync(
                    action, folder, parent, newName, description, template, folderType, namingScheme, force, paths, gate, ct),
                cancellationToken));

    private async Task<object> ManageCoreAsync(
        string action,
        string folder,
        string? parent,
        string? newName,
        string? description,
        string? template,
        string? folderType,
        string? namingScheme,
        bool force,
        string[]? paths,
        ConfirmationGate gate,
        CancellationToken cancellationToken)
    {
        return action.Trim().ToLowerInvariant() switch
        {
            "create" when paths is { Length: > 0 } =>
                await CreateManyAsync(paths, parent, description, folderType, namingScheme, gate, cancellationToken),
            "create" => await CreateAsync(folder, parent, description, folderType, namingScheme, cancellationToken),
            "rename" => await RenameAsync(folder, newName, description, cancellationToken),
            "move" => await MoveAsync(folder, parent, cancellationToken),
            "delete" => await DeleteAsync(folder, force, gate, cancellationToken),
            "set_template" => await SetTemplateAsync(folder, template, cancellationToken),
            "repair_system" => await RepairSystemAsync(folder, gate, cancellationToken),
            _ => throw new ArgumentException(
                $"Unknown action '{action}'. Allowed: create, rename, move, delete, set_template, repair_system.",
                nameof(action)),
        };
    }

    [McpServerTool(Name = "vault_folder_types", ReadOnly = true)]
    [Description("""
        Vault folder types. The type determines what the folder accepts: altium-component-library
        for parts, altium-symbol-library for symbols, altium-pcb-component-library for
        footprints, altium-component-template-catalog for templates.
        """)]
    public async Task<object> ListFolderTypesAsync(CancellationToken cancellationToken)
    {
        var types = await _workspace.Gateway.GetFolderTypesAsync(cancellationToken);

        return new
        {
            count = types.Count,
            folderTypes = types
                .OrderBy(type => type.HRID, StringComparer.OrdinalIgnoreCase)
                .Select(type => new { name = type.HRID, guid = type.GUID })
                .ToList(),
        };
    }

    private async Task<object> CreateAsync(
        string name,
        string? parent,
        string? description,
        string? folderType,
        string? namingScheme,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new ArgumentException("To create a folder, specify parent — where to create it.", nameof(parent));
        }

        _workspace.Guard.EnsureAllowed(Operation, 1, [parent]);

        (FolderNode created, string? actualNamingScheme) = await _workspace.Folders.CreateAsync(
            name, parent, description, folderType, namingScheme, attributes: 0, cancellationToken);

        await AuditAsync("create", 1, $"Folder created '{created.Path}'", cancellationToken);

        return new { created = true, path = created.Path, guid = created.Guid, namingScheme = actualNamingScheme };
    }

    /// <summary>How many folders are created per call; the rest goes to <c>remaining</c>.</summary>
    private const int MaxFoldersPerCall = 200;

    private async Task<object> CreateManyAsync(
        string[] paths,
        string? parent,
        string? description,
        string? folderType,
        string? namingScheme,
        ConfirmationGate gate,
        CancellationToken cancellationToken)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var full = paths;
        if (!string.IsNullOrWhiteSpace(parent))
        {
            string parentPath = (await _workspace.Catalog.ResolveFolderAsync(parent, cancellationToken)).Path;
            full = paths.Select(path => parentPath + "\\" + path.Trim().TrimStart('\\', '/')).ToArray();
        }

        FolderCreationPlan plan = await _workspace.Folders.PlanCreateManyAsync(full, cancellationToken);

        if (plan.Conflicts.Count > 0)
        {
            throw new InvalidOperationException(
                $"Refused before writing, {plan.Conflicts.Count} conflicts: {string.Join("; ", plan.Conflicts.Take(20))}. "
                + "Fix the path list and repeat — nothing was created.");
        }

        if (plan.ToCreate.Count == 0)
        {
            return new
            {
                created = 0,
                skipped = plan.Skipped,
                note = "All the listed folders already exist.",
                elapsedMs = watch.ElapsedMilliseconds,
            };
        }

        var touched = plan.ToCreate.Select(folder => folder.Path).ToList();

        if (await gate.DeferIfLargeAsync(
                plan.ToCreate.Count,
                touched,
                $"Creating {plan.ToCreate.Count} folders",
                touched.Select(path => (object)$"create: {path}").ToList(),
                cancellationToken) is { } confirmation)
        {
            return confirmation;
        }

        var result = await _workspace.Folders.CreateManyAsync(
            plan, full, MaxFoldersPerCall, description, folderType, namingScheme, cancellationToken);

        var createdPaths = result.Created.Select(folder => folder.Path).ToList();
        var createdSet = createdPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remaining = plan.ToCreate.Where(folder => !createdSet.Contains(folder.Path)).Select(folder => folder.Path).ToList();

        PlanChargeReceipt? planReceipt = _workspace.Guard.ChargeIfPlanned(Operation, createdPaths.Count, touched);

        await AuditAsync(
            "create", createdPaths.Count, $"Folders created: {createdPaths.Count}, existing skipped: {plan.Skipped.Count}",
            cancellationToken, planReceipt?.Number);

        return new
        {
            created = createdPaths.Count,
            createdPaths = createdPaths.Take(100).ToList(),
            createdOmitted = createdPaths.Count > 100 ? createdPaths.Count - 100 : (int?)null,
            skipped = plan.Skipped,
            failed = Array.Empty<string>(),
            remaining = remaining.Count,
            remainingPaths = remaining.Take(50).ToList(),
            note = remaining.Count > 0
                ? $"At most {MaxFoldersPerCall} folders are created per call; repeat the same call — the created ones will be skipped."
                : null,
            strategy = result.Strategy,
            elapsedMs = watch.ElapsedMilliseconds,
            plan = planReceipt?.Describe(),
        };
    }

    private async Task<object> RenameAsync(
        string folder,
        string? newName,
        string? description,
        CancellationToken cancellationToken)
    {
        if (newName is null && description is null)
        {
            throw new ArgumentException("Specify newName and/or description.", nameof(newName));
        }

        _workspace.Guard.EnsureAllowed(Operation, 1, [folder]);

        FolderNode updated = await _workspace.Folders.UpdateAsync(
            folder, newName, description, cancellationToken);

        await AuditAsync("rename", 1, $"Folder renamed to '{updated.Path}'", cancellationToken);

        return new { renamed = true, path = updated.Path, guid = updated.Guid };
    }

    private async Task<object> MoveAsync(string folder, string? parent, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new ArgumentException("To move a folder, specify parent.", nameof(parent));
        }

        _workspace.Guard.EnsureAllowed(Operation, 1, [folder, parent]);

        FolderNode moved = await _workspace.Folders.MoveAsync(folder, parent, cancellationToken);

        await AuditAsync("move", 1, $"Folder moved: '{moved.Path}'", cancellationToken);

        return new { moved = true, path = moved.Path, guid = moved.Guid };
    }

    private async Task<object> DeleteAsync(string folder, bool force, ConfirmationGate gate, CancellationToken cancellationToken)
    {
        FolderService.FolderDeletionPlan plan = await _workspace.Folders.PlanDeleteAsync(folder, cancellationToken);

        // Without force and with a non-empty folder — refuse in advance instead of sending a request that is bound to be rejected.
        if (!force && plan.Items.Count > 0)
        {
            throw new InvalidOperationException(
                $"Folder '{plan.Root.Path}' contains {plan.Items.Count} items"
                + (plan.Subtree.Count > 1 ? $" and {plan.Subtree.Count - 1} nested folders" : string.Empty)
                + ". Move them (vault_move_items) or repeat with force=true — the items will go to the trash.");
        }

        // A link from a live part or template outside the subtree — a refusal even with force.
        var blocked = await _workspace.DeletionGuard.FindBlockingAsync(
            plan.Items.Select(item => (item.GUID, item.HRID ?? item.GUID)).ToList(), cancellationToken);
        if (blocked.Count > 0)
        {
            throw new InvalidOperationException(FolderService.DescribeBlocked(plan.Root, blocked));
        }

        var preview = plan.Subtree.Select(node => (object)$"folder: {node.Path}")
            .Concat(plan.Items.Select(item => (object)$"{item.HRID ?? item.GUID}"))
            .ToList();

        if (await gate.DeferIfLargeAsync(
                plan.ObjectCount,
                plan.Subtree.Select(node => node.Path).ToList(),
                $"Moving to the trash '{plan.Root.Path}': folders {plan.Subtree.Count}, items {plan.Items.Count}",
                preview,
                cancellationToken) is { } confirmation)
        {
            return confirmation;
        }

        int removed = await _workspace.Folders.DeleteAsync(plan, force, cancellationToken);

        PlanChargeReceipt? planReceipt = _workspace.Guard.ChargeIfPlanned(
            Operation, plan.ObjectCount, plan.Subtree.Select(node => node.Path).ToList());

        var deletedEntries = plan.Subtree
            .Select(node => (object)new { kind = "folder", hrid = node.Path, guid = node.Guid })
            .Concat(plan.Items.Select(item => (object)new { kind = "item", hrid = item.HRID ?? item.GUID, guid = item.GUID }))
            .ToList();

        const int AuditLimit = 200;

        await _workspace.Audit.WriteAsync(new AuditEntry
        {
            Operation = $"{Operation}:delete",
            Outcome = "done",
            Affected = plan.ObjectCount,
            Plan = planReceipt?.Number,
            Summary = $"Folder '{plan.Root.Path}' sent to the trash: folders {removed}, items {plan.Items.Count}",
            Before = deletedEntries.Count <= AuditLimit
                ? deletedEntries
                : deletedEntries.Take(AuditLimit)
                    .Append((object)new { note = $"{deletedEntries.Count - AuditLimit} more objects, not listed" })
                    .ToList(),
        }, cancellationToken);

        return new
        {
            deleted = true,
            folders = removed,
            items = plan.Items.Count,
            path = plan.Root.Path,
            note = "The folder and its items were moved to the trash; to restore — vault_restore_items action=list, then restore.",
            plan = planReceipt?.Describe(),
        };
    }

    private async Task<object> SetTemplateAsync(
        string folder,
        string? template,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            throw new ArgumentException("Specify template — the component template.", nameof(template));
        }

        _workspace.Guard.EnsureAllowed(Operation, 1, [folder]);

        var (node, assigned) = await _workspace.Templates.AssignToFolderAsync(
            folder, template, cancellationToken);

        await AuditAsync(
            "set_template", 1, $"Template {assigned.Hrid} assigned to folder '{node.Path}'", cancellationToken);

        return new
        {
            assigned = true,
            folder = node.Path,
            template = assigned.Hrid,
            componentTypeGuid = assigned.ComponentTypeGuid,
            note = "The template applies to parts created in the folder from now on. "
                + "For existing ones use vault_set_links with the template role.",
        };
    }

    /// <summary>Brings the Datasheets folders to the Altium reference; shows what differed.</summary>
    private async Task<object> RepairSystemAsync(string folder, ConfirmationGate gate, CancellationToken cancellationToken)
    {
        var reports = (await _workspace.Folders.InspectSystemFoldersAsync(
                string.IsNullOrWhiteSpace(folder) ? null : folder, cancellationToken))
            .Where(report => report.Differences.Count > 0)
            .ToList();

        if (reports.Count == 0)
        {
            return new { repaired = 0, note = "All Datasheets folders are already like Altium's: type, system bit and naming scheme." };
        }

        if (await gate.DeferIfLargeAsync(
                reports.Count,
                reports.Select(report => report.Folder.Path).ToList(),
                $"Bringing {reports.Count} Datasheets folders to the reference",
                reports.Select(report => (object)$"{report.Folder.Path}: {string.Join("; ", report.Differences)}").ToList(),
                cancellationToken) is { } confirmation)
        {
            return confirmation;
        }

        var repaired = await _workspace.Folders.RepairSystemFoldersAsync(
            string.IsNullOrWhiteSpace(folder) ? null : folder, cancellationToken);

        PlanChargeReceipt? planReceipt = _workspace.Guard.ChargeIfPlanned(
            Operation, repaired.Count, repaired.Select(report => report.Folder.Path).Distinct().ToList());

        await AuditAsync(
            "repair_system", repaired.Count, $"Datasheets folders brought to the Altium reference: {repaired.Count}",
            cancellationToken, planReceipt?.Number);

        return new
        {
            repaired = repaired.Count,
            folders = repaired.Take(30).Select(report => new { path = report.Folder.Path, was = report.Differences }).ToList(),
            foldersOmitted = repaired.Count > 30 ? repaired.Count - 30 : (int?)null,
            note = "Folder contents were not changed. Whether they are hidden in Explorer, check there: Show System Folders must be off.",
            plan = planReceipt?.Describe(),
        };
    }

    private Task AuditAsync(
        string action, int affected, string summary, CancellationToken cancellationToken, int? plan = null) =>
        _workspace.Audit.WriteAsync(new AuditEntry
        {
            Operation = $"{Operation}:{action}",
            Outcome = "done",
            Affected = affected,
            Plan = plan,
            Summary = summary,
        }, cancellationToken);
}
