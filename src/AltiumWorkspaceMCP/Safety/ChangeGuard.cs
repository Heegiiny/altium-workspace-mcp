using System.Collections.Concurrent;
using System.Security.Cryptography;
using AltiumWorkspaceMCP.Configuration;

namespace AltiumWorkspaceMCP.Safety;

/// <summary>A deferred change waiting for confirmation.</summary>
/// <param name="Payload">
/// Data for re-applying the change (or a short description if <paramref name="Execute"/> is set).
/// </param>
/// <param name="Execute">
/// How to apply the change after confirmation: repeat the tool call with the same
/// arguments. Empty — the tool restores the edit from <paramref name="Payload"/> itself.
/// </param>
public sealed record PendingChange(
    string Token,
    string Operation,
    int Affected,
    string Summary,
    object Payload,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    Func<CancellationToken, Task<object>>? Execute = null);

/// <summary>The change is forbidden by the safety rules.</summary>
public sealed class ChangeRejectedException(string message) : Exception(message);

/// <summary>
/// The rules by which a change is allowed to run: write mode,
/// allowed folders and confirmation of large edits.
/// </summary>
/// <remarks>
/// Confirmation of bulk edits works the same for all write tools: an edit
/// above the guarded-mode threshold is not applied but deferred — the tool returns
/// a preview and a token, and the same edit is applied by a repeated call with
/// <c>confirmToken</c>. The preview is built by a dry run of the same operation, so
/// the owner sees the real changes, not just their count.
/// </remarks>
public sealed class ChangeGuard
{
    /// <summary>How long an unconfirmed plan lives. After that it is considered stale.</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(30);

    /// <summary>How many preview items go into the response.</summary>
    public const int PreviewLimit = 20;

    /// <summary>Work period of a plan if the owner did not specify hours.</summary>
    public static readonly TimeSpan DefaultPlanLifetime = TimeSpan.FromHours(2);

    /// <summary>Longest work period of a plan — beyond it a new approval is required.</summary>
    public static readonly TimeSpan MaxPlanLifetime = TimeSpan.FromHours(8);

    private readonly VaultOptions _options;
    private readonly ConcurrentDictionary<string, PendingChange> _pending = new(StringComparer.Ordinal);
    private readonly Lock _planGate = new();
    private WorkPlan? _plan;
    private int _planSequence;

    public ChangeGuard(VaultOptions options) => _options = options;

    public WriteMode Mode => _options.WriteMode;

    public int ConfirmThreshold => _options.ConfirmThreshold;

    public IReadOnlyList<string> WritableFolders => _options.WritableFolders;

    /// <summary>
    /// Checks whether a change is allowed: write mode, allowed folders, non-emptiness.
    /// For single-object edits (folder, template, type): one object needs no confirmation.
    /// </summary>
    /// <param name="folderPaths">
    /// Folder paths of the affected objects — checked against the allowed list.
    /// </param>
    public void EnsureAllowed(
        string operation,
        int affected,
        IReadOnlyCollection<string> folderPaths) =>
        _ = RequiresConfirmation(operation, affected, folderPaths);

    /// <summary>
    /// Checks that a change is allowed (like <see cref="EnsureAllowed"/>) and says whether
    /// it needs confirmation: <see langword="true"/> — the edit is above the guarded-mode threshold.
    /// Write tools do not call it directly but go through <see cref="ConfirmationGate"/>
    /// or <see cref="TryDefer"/>, so that the result of the check cannot be lost.
    /// </summary>
    public bool RequiresConfirmation(
        string operation,
        int affected,
        IReadOnlyCollection<string> folderPaths)
    {
        // A dry run writes nothing, so it is available in read-only mode too
        // and needs no confirmation; the folder restriction it still shows honestly.
        bool dryRun = Vault.DryRun.IsActive;

        if (_options.WriteMode == WriteMode.ReadOnly && !dryRun)
        {
            throw new ChangeRejectedException(
                $"Operation '{operation}' rejected: the server is in read-only mode "
                + "(ALTIUM_WRITE_MODE=readonly). Writes can be allowed by switching the mode to guarded.");
        }

        EnsureFoldersWritable(operation, folderPaths);

        if (affected == 0)
        {
            throw new ChangeRejectedException(
                $"Operation '{operation}' affects no objects — nothing to do.");
        }

        if (dryRun || _options.WriteMode == WriteMode.Unguarded || affected <= _options.ConfirmThreshold)
        {
            return false;
        }

        // An approved active plan lifts the confirmation from every portion —
        // the owner has already approved the whole work.
        return !PlanCovers(operation, affected, folderPaths);
    }

    private bool PlanCovers(string operation, int affected, IReadOnlyCollection<string> folderPaths)
    {
        lock (_planGate)
        {
            return _plan is { } plan && plan.CanCover(operation, affected, folderPaths, DateTimeOffset.Now);
        }
    }

    /// <summary>
    /// Path for edits where the tool itself knows what to apply: <paramref name="execute"/>
    /// runs the already verified plan. <see langword="null"/> — it can be applied at once, otherwise —
    /// a deferred change with a token.
    /// </summary>
    public PendingChange? TryDefer(
        string operation,
        int affected,
        IReadOnlyCollection<string> folderPaths,
        string summary,
        object payload,
        Func<CancellationToken, Task<object>>? execute = null) =>
        RequiresConfirmation(operation, affected, folderPaths)
            ? Defer(operation, affected, summary, payload, execute)
            : null;

    /// <summary>
    /// Runs the body of a write tool. With <paramref name="confirmToken"/> it applies what
    /// was shown in the preview, and without it runs the body with the confirmation gate.
    /// </summary>
    /// <remarks>
    /// With a token the other call arguments are ignored: the edit the owner saw is
    /// executed. A dry run does not consume the token.
    /// </remarks>
    public Task<object> RunAsync(
        string operation,
        string? confirmToken,
        Func<ConfirmationGate, CancellationToken, Task<object>> body,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(confirmToken) && !Vault.DryRun.IsActive)
        {
            PendingChange pending = Consume(confirmToken.Trim(), operation);

            return pending.Execute is { } execute
                ? execute(cancellationToken)
                : throw new ChangeRejectedException(
                    $"Token '{confirmToken}' does not belong to operation '{operation}'.");
        }

        return body(new ConfirmationGate(this, operation, body, approved: null), cancellationToken);
    }

    /// <summary>Rejects a write outside the allowed folders if a list is set.</summary>
    private void EnsureFoldersWritable(string operation, IReadOnlyCollection<string> folderPaths)
    {
        if (_options.WritableFolders.Count == 0)
        {
            return;
        }

        var blocked = folderPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(path => !_options.WritableFolders.Any(
                allowed => path.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (blocked.Count > 0)
        {
            throw new ChangeRejectedException(
                $"Operation '{operation}' rejected: writes are allowed only in "
                + $"{string.Join(", ", _options.WritableFolders)}. "
                + $"Outside these folders: {string.Join(", ", blocked)}.");
        }
    }

    /// <summary>Defers the change and issues a token that confirms it.</summary>
    public PendingChange Defer(
        string operation,
        int affected,
        string summary,
        object payload,
        Func<CancellationToken, Task<object>>? execute = null)
    {
        PurgeExpired();

        DateTimeOffset now = DateTimeOffset.Now;

        var change = new PendingChange(
            Token: GenerateToken(),
            Operation: operation,
            Affected: affected,
            Summary: summary,
            Payload: payload,
            CreatedAt: now,
            ExpiresAt: now + TokenLifetime,
            Execute: execute);

        _pending[change.Token] = change;
        return change;
    }

    /// <summary>
    /// Tool response instead of applying the edit: what will change and the token that
    /// confirms it. One shape for all write tools.
    /// </summary>
    /// <param name="preview">What will change; at most <see cref="PreviewLimit"/> items go into the response.</param>
    /// <param name="previewTotal">How many preview items there were before shortening.</param>
    public object Confirmation(PendingChange change, IReadOnlyList<object> preview, int? previewTotal = null)
    {
        int total = Math.Max(previewTotal ?? preview.Count, preview.Count);
        var shown = preview.Take(PreviewLimit).ToList();

        return new
        {
            confirmationRequired = true,
            applied = 0,
            operation = change.Operation,
            affected = change.Affected,
            preview = shown,
            previewNote = total > shown.Count
                ? $"Showing {shown.Count} of {total}; the rest will change the same way."
                : null,
            confirmToken = change.Token,
            expiresAt = change.ExpiresAt,
            note = $"Guarded mode: the edit affects {change.Affected} objects, while up to "
                + $"{ConfirmThreshold} are allowed without confirmation. Check the preview and repeat this same call "
                + $"with confirmToken '{change.Token}' — the other parameters are not needed. "
                + $"The token is valid until {change.ExpiresAt:HH:mm:ss}.",
        };
    }

    /// <summary>
    /// Takes the deferred change by token. The token is single-use: confirming
    /// the same plan twice is impossible.
    /// </summary>
    public PendingChange Consume(string token, string expectedOperation)
    {
        PurgeExpired();

        if (!_pending.TryRemove(token, out PendingChange? change))
        {
            throw new ChangeRejectedException(
                $"Confirmation token '{token}' is unknown or already used. "
                + $"Tokens are valid for {TokenLifetime.TotalMinutes:N0} minutes — build the preview again "
                + "(the same call without confirmToken).");
        }

        if (!string.Equals(change.Operation, expectedOperation, StringComparison.Ordinal))
        {
            throw new ChangeRejectedException(
                $"Token '{token}' was issued for operation '{change.Operation}', "
                + $"but is being used to confirm '{expectedOperation}'.");
        }

        return change;
    }

    public IReadOnlyList<PendingChange> ListPending()
    {
        PurgeExpired();
        return _pending.Values.OrderBy(change => change.CreatedAt).ToList();
    }

    // ── Plan for the whole work ─────────────────────────────

    /// <summary>The active plan, if any — approved or still waiting for approval.</summary>
    public WorkPlan? CurrentPlan
    {
        get { lock (_planGate) return _plan; }
    }

    /// <summary>
    /// Creates a plan: which operations it covers, in which folders and how many objects
    /// at most. One plan per server process — if one is already active, it is refused
    /// with a hint to close it.
    /// </summary>
    public WorkPlan CreatePlan(
        string summary,
        IReadOnlyList<string> operations,
        IReadOnlyList<string> folders,
        int maxObjects,
        double hours)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new ChangeRejectedException("Specify summary — one line: what the plan does and why.");
        }

        if (operations.Count == 0)
        {
            throw new ChangeRejectedException(
                "Specify operations — which write tools the plan covers, for example vault_update_parameters.");
        }

        if (folders.Count == 0)
        {
            throw new ChangeRejectedException(
                "Specify folders — root folders where the plan allows writes without confirmation on every call.");
        }

        if (maxObjects <= 0)
        {
            throw new ChangeRejectedException("maxObjects must be a positive number.");
        }

        if (hours < 0)
        {
            throw new ChangeRejectedException("hours cannot be negative.");
        }

        if (hours > MaxPlanLifetime.TotalHours)
        {
            throw new ChangeRejectedException(
                $"hours cannot exceed {MaxPlanLifetime.TotalHours:N0} — the owner will not approve a plan for that long; "
                + "create a new plan when this one runs out.");
        }

        TimeSpan lifetime = hours == 0 ? DefaultPlanLifetime : TimeSpan.FromHours(hours);

        lock (_planGate)
        {
            DateTimeOffset now = DateTimeOffset.Now;

            if (_plan is { } existing && !existing.IsExpired(now))
            {
                throw new ChangeRejectedException(
                    $"Plan #{existing.Number} is already active ({(existing.Approved ? "approved" : "waiting for approval")}, "
                    + $"remaining {existing.Remaining} of {existing.MaxObjects}, until {existing.ExpiresAt:HH:mm}). "
                    + "Close it (vault_plan close) and create a new one.");
            }

            _planSequence++;
            var plan = new WorkPlan(GenerateToken(), _planSequence, summary, operations, folders, maxObjects, now, lifetime);
            _plan = plan;
            return plan;
        }
    }

    /// <summary>
    /// Approves the active plan on behalf of the owner — the agent calls this only after
    /// showing the plan summary to the owner and getting a "yes" (an agent rule, not a server one).
    /// </summary>
    public WorkPlan ApprovePlan(string planToken)
    {
        lock (_planGate)
        {
            if (_plan is not { } plan || !string.Equals(plan.Token, planToken.Trim(), StringComparison.Ordinal))
            {
                throw new ChangeRejectedException(
                    $"Plan token '{planToken}' does not belong to the active plan. Create the plan again: vault_plan create.");
            }

            DateTimeOffset now = DateTimeOffset.Now;

            if (plan.IsExpired(now))
            {
                throw new ChangeRejectedException(
                    $"Plan #{plan.Number} expired at {plan.ExpiresAt:HH:mm}. Create the plan again: vault_plan create.");
            }

            plan.Approve(now);
            return plan;
        }
    }

    /// <summary>Closes the active plan, if there is one.</summary>
    public WorkPlan? ClosePlan()
    {
        lock (_planGate)
        {
            WorkPlan? closed = _plan;
            _plan = null;
            return closed;
        }
    }

    /// <summary>
    /// Charges the really changed objects to the active plan if the edit
    /// fits it (operation, folders, approval, period) — otherwise does nothing.
    /// </summary>
    /// <remarks>
    /// Called after a successful write, not during the confirmation check: a dry
    /// run never gets here, and what is charged is the number of applied edits
    /// (<paramref name="applied"/>), not the declared object count before the write.
    /// </remarks>
    public PlanChargeReceipt? ChargeIfPlanned(string operation, int applied, IReadOnlyCollection<string> folderPaths)
    {
        // A dry run "applies" the edit on paper only (DryRunRecorder), so
        // the same results.Count leaks in here as in a real write; without this
        // check the preview would charge the plan without changing anything on the server.
        if (applied <= 0 || Vault.DryRun.IsActive)
        {
            return null;
        }

        lock (_planGate)
        {
            DateTimeOffset now = DateTimeOffset.Now;

            if (_plan is not { } plan
                || !plan.Approved
                || plan.IsExpired(now)
                || !plan.SupportsOperation(operation)
                || !plan.FoldersWithinRoots(folderPaths))
            {
                return null;
            }

            plan.Charge(applied);
            return new PlanChargeReceipt(plan.Number, plan.Remaining, plan.MaxObjects, plan.ExpiresAt);
        }
    }

    private void PurgeExpired()
    {
        DateTimeOffset threshold = DateTimeOffset.Now - TokenLifetime;
        foreach (var entry in _pending.Where(entry => entry.Value.CreatedAt < threshold).ToList())
        {
            _pending.TryRemove(entry.Key, out _);
        }
    }

    private static string GenerateToken() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
}
