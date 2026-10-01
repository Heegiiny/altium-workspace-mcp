using System.ComponentModel;
using AltiumWorkspaceMCP.Safety;
using AltiumWorkspaceMCP.Vault;
using ModelContextProtocol.Server;

namespace AltiumWorkspaceMCP.Tools;

/// <summary>
/// Approving the work as a whole instead of confirming each portion:
/// the agent describes a plan — which tools, in which folders, how many
/// objects at most and for how long — and after the owner's explicit "yes" approves it.
/// While the plan is active, a write that fits it goes without a preview and a token.
/// </summary>
[McpServerToolType]
public sealed class PlanTools
{
    /// <summary>Operations that need a separate warning: they move objects to the trash.</summary>
    private static readonly string[] DestructiveOperations = ["vault_delete_items", "vault_folder"];

    private readonly VaultWorkspace _workspace;

    public PlanTools(VaultWorkspace workspace) => _workspace = workspace;

    [McpServerTool(Name = "vault_plan", Destructive = false, Idempotent = false)]
    [Description("""
        One owner confirmation for the whole bulk job, not for each portion: otherwise the agent splits edits into calls of 4 parts to avoid
        hitting the confirmation threshold — the work runs many times slower than the server.

        Order: create builds the plan and returns planToken — show the summary to the owner and
        wait for an explicit "yes", only then approve planToken=…. While an approved plan
        is active, a write by a tool from operations, in folders from folders, fitting the
        remaining volume, goes without a preview and a token; the remainder is charged after a successful
        write by the number of really changed objects (a dry run does not use up the volume).
        A write outside the plan (another tool, another folder, volume or period beyond the remainder) follows
        the old rules: preview and confirmToken.

        One active plan per server process: create while one is active is refused, close the
        previous one (close). The plan is not kept after a server restart — a new one is needed.

        Actions:
          create  — create a plan: summary, operations, folders, maxObjects, hours;
          approve — approve the plan after the owner's "yes": planToken;
          show    — state of the active plan: remainder, period, latest operations;
          close   — close the plan early.
        """)]
    public async Task<object> ManageAsync(
        [Description("create, approve, show or close.")]
        string action = "show",
        [Description("For create — one line: what the plan does and why.")]
        string? summary = null,
        [Description("""
            For create — which write tools the plan covers, for example
            ["vault_update_parameters", "vault_table_write", "vault_set_links", "vault_model_files"]. Deletion
            (vault_delete_items, vault_folder) falls under the plan only if named here explicitly.
            """)]
        string[]? operations = null,
        [Description("For create — root folders of the plan: full path or path ending. A write outside them is confirmed as before.")]
        string[]? folders = null,
        [Description("For create — the most objects the plan allows to change.")]
        int maxObjects = 0,
        [Description("For create — plan validity period in hours; default 2, at most 8.")]
        double hours = 0,
        [Description("For approve — token from the create response.")]
        string? planToken = null,
        CancellationToken cancellationToken = default)
    {
        return action.Trim().ToLowerInvariant() switch
        {
            "create" => await CreateAsync(summary, operations, folders, maxObjects, hours, cancellationToken),
            "approve" => Approve(planToken),
            "show" => Show(),
            "close" => await CloseAsync(cancellationToken),
            _ => throw new ArgumentException(
                $"Unknown action '{action}'. Allowed: create, approve, show, close.", nameof(action)),
        };
    }

    private async Task<object> CreateAsync(
        string? summary,
        string[]? operations,
        string[]? folders,
        int maxObjects,
        double hours,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new ArgumentException("Specify summary — one line: what the plan does and why.", nameof(summary));
        }

        if (folders is not { Length: > 0 })
        {
            throw new ArgumentException(
                "Specify folders — root folders where the plan allows writes without confirmation on every call.",
                nameof(folders));
        }

        // Plan roots are stored as full paths: a short address entered by the owner
        // is resolved once, right here — then comparison needs no server call.
        var resolvedFolders = new List<string>(folders.Length);
        foreach (string folder in folders)
        {
            FolderNode node = await _workspace.Catalog.ResolveFolderAsync(folder, cancellationToken);
            resolvedFolders.Add(node.Path);
        }

        WorkPlan plan = _workspace.Guard.CreatePlan(
            summary, operations ?? [], resolvedFolders, maxObjects, hours);

        await AuditAsync(
            "create", 0,
            $"Plan #{plan.Number} created: '{plan.Summary}', volume {plan.MaxObjects}, "
            + $"operations {string.Join(", ", plan.Operations)}, folders {string.Join(", ", plan.Folders)}",
            plan.Number, cancellationToken);

        bool destructive = plan.Operations.Any(operation => DestructiveOperations.Contains(operation, StringComparer.Ordinal));

        return new
        {
            planToken = plan.Token,
            number = plan.Number,
            summary = plan.Summary,
            operations = plan.Operations,
            folders = plan.Folders,
            maxObjects = plan.MaxObjects,
            expiresAt = plan.ExpiresAt,
            destructiveOperationsIncluded = destructive
                ? "The plan's operations include moving to the trash — show this to the owner separately."
                : null,
            note = "The plan lives in process memory: a server restart cancels it, a new one is needed. "
                + "The period starts anew at approve. Show this summary to the owner and after an explicit \"yes\" "
                + $"call vault_plan action=approve planToken=\"{plan.Token}\".",
        };
    }

    private object Approve(string? planToken)
    {
        if (string.IsNullOrWhiteSpace(planToken))
        {
            throw new ArgumentException("Specify planToken from the vault_plan create response.", nameof(planToken));
        }

        WorkPlan plan = _workspace.Guard.ApprovePlan(planToken);

        _ = AuditAsync(
            "approve", 0, $"Plan #{plan.Number} approved, valid until {plan.ExpiresAt:HH:mm}",
            plan.Number, CancellationToken.None);

        return new
        {
            approved = true,
            number = plan.Number,
            summary = plan.Summary,
            operations = plan.Operations,
            folders = plan.Folders,
            maxObjects = plan.MaxObjects,
            expiresAt = plan.ExpiresAt,
            note = "A write by a tool from operations in folders from folders within the remainder now goes without "
                + "a preview and a token. Remainder and period — vault_plan show or vault_apply_status.",
        };
    }

    private object Show()
    {
        WorkPlan? plan = _workspace.Guard.CurrentPlan;

        if (plan is null)
        {
            return new { active = false, note = "No active plan. Create one: vault_plan create." };
        }

        bool expired = plan.IsExpired(DateTimeOffset.Now);

        return new
        {
            active = !expired,
            number = plan.Number,
            approved = plan.Approved,
            expired,
            summary = plan.Summary,
            operations = plan.Operations,
            folders = plan.Folders,
            maxObjects = plan.MaxObjects,
            applied = plan.Applied,
            remaining = plan.Remaining,
            createdAt = plan.CreatedAt,
            approvedAt = plan.ApprovedAt,
            expiresAt = plan.ExpiresAt,
            note = expired
                ? "The plan expired: writes under it no longer go without confirmation. Create a new one (vault_plan create)."
                : plan.Approved
                    ? null
                    : "The plan is not approved yet: writes under it need the usual confirmation until approve.",
        };
    }

    private async Task<object> CloseAsync(CancellationToken cancellationToken)
    {
        WorkPlan? closed = _workspace.Guard.ClosePlan();

        if (closed is null)
        {
            return new { closed = false, note = "There was no active plan." };
        }

        await AuditAsync(
            "close", 0,
            $"Plan #{closed.Number} closed: applied {closed.Applied} of {closed.MaxObjects}",
            closed.Number, cancellationToken);

        return new
        {
            closed = true,
            number = closed.Number,
            applied = closed.Applied,
            maxObjects = closed.MaxObjects,
            note = "The next bulk edit again needs a preview and confirmToken until a new plan is created.",
        };
    }

    private Task AuditAsync(string action, int affected, string summary, int planNumber, CancellationToken cancellationToken) =>
        _workspace.Audit.WriteAsync(new AuditEntry
        {
            Operation = $"vault_plan:{action}",
            Outcome = "done",
            Affected = affected,
            Plan = planNumber,
            Summary = summary,
        }, cancellationToken);
}
