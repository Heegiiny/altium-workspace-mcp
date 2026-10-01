using System.Text.Encodings.Web;
using System.Text.Json;
using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Safety;

/// <summary>
/// Confirmation gate in the body of a write tool: the place where the tool already knows how many
/// objects it will affect and either goes on or returns a preview with a token.
/// </summary>
/// <remarks>
/// The gate is created by <see cref="ChangeGuard.RunAsync"/>. The token holds a closure that
/// repeats the tool body with the same arguments and an already open gate, so
/// after confirmation exactly the operation that was shown is executed.
/// </remarks>
public sealed class ConfirmationGate
{
    private readonly ChangeGuard _guard;
    private readonly Func<ConfirmationGate, CancellationToken, Task<object>> _body;
    private readonly int? _approved;

    internal ConfirmationGate(
        ChangeGuard guard,
        string operation,
        Func<ConfirmationGate, CancellationToken, Task<object>> body,
        int? approved)
    {
        _guard = guard;
        Operation = operation;
        _body = body;
        _approved = approved;
    }

    public string Operation { get; }

    /// <summary>The edit is already confirmed by a token.</summary>
    public bool Confirmed => _approved is not null;

    /// <summary>
    /// Checks that the edit is allowed and, if it is above the threshold, defers it.
    /// Returns the response for the client (preview and token) — the tool must return it
    /// and write nothing; <see langword="null"/> — the edit can be applied.
    /// </summary>
    /// <param name="affected">How many objects the edit will affect.</param>
    /// <param name="folderPaths">Folders of the affected objects — for the allowed list.</param>
    /// <param name="summary">One line for <c>vault_apply_status</c>.</param>
    /// <param name="preview">
    /// A ready preview. Empty — it is built by a dry run of the same tool body.
    /// </param>
    public async Task<object?> DeferIfLargeAsync(
        int affected,
        IReadOnlyCollection<string> folderPaths,
        string summary,
        IReadOnlyList<object>? preview = null,
        CancellationToken cancellationToken = default)
    {
        bool requires = _guard.RequiresConfirmation(Operation, affected, folderPaths);

        if (_approved is { } approved)
        {
            // The data may have changed between the preview and the confirmation: an edit
            // affecting more objects than the owner saw is not executed.
            if (affected > approved)
            {
                throw new ChangeRejectedException(
                    $"Operation '{Operation}' now affects {affected} objects, but {approved} were confirmed: "
                    + "the data changed after the preview. Build the preview again (a call without confirmToken).");
            }

            return null;
        }

        if (!requires)
        {
            return null;
        }

        int total = preview?.Count ?? 0;

        if (preview is null)
        {
            (object result, DryRunRecorder recorder) = await RehearseAsync(affected, cancellationToken);

            // A dry run wrote nothing: the edit is in fact empty (links intact, values
            // already the same), there is nothing to confirm — the response is the same as a real edit would give.
            if (recorder.Writes.Count == 0)
            {
                return new
                {
                    confirmationRequired = false,
                    applied = 0,
                    note = "Nothing to change: a trial run of the operation would write nothing, no confirmation is needed.",
                    result,
                };
            }

            (preview, total) = DryRunPreview.FromRecorder(recorder);
        }

        PendingChange change = _guard.Defer(
            Operation,
            affected,
            summary,
            payload: summary,
            execute: ct => _body(new ConfirmationGate(_guard, Operation, _body, approved: affected), ct));

        return _guard.Confirmation(change, preview, total);
    }

    /// <summary>Trial run: the same tool body executed as a dry run.</summary>
    private async Task<(object Result, DryRunRecorder Recorder)> RehearseAsync(
        int affected,
        CancellationToken cancellationToken)
    {
        var rehearsal = new ConfirmationGate(_guard, Operation, _body, approved: affected);

        return await DryRun.CaptureAsync(Operation, () => _body(rehearsal, cancellationToken));
    }
}

/// <summary>Compresses dry-run records into a short preview for confirmation.</summary>
public static class DryRunPreview
{
    /// <summary>Length of one preview line.</summary>
    public const int ItemLength = 240;

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Lines of the form "operation: compressed record" and the total record count before shortening.</summary>
    public static (IReadOnlyList<object> Items, int Total) FromRecorder(DryRunRecorder recorder)
    {
        var items = new List<object>();
        int total = 0;

        foreach (DryRunWrite write in recorder.Writes)
        {
            total += write.Count;

            foreach (object sample in write.Sample)
            {
                items.Add($"{write.Operation}: {Shorten(sample)}");
            }
        }

        return (items, Math.Max(total, items.Count));
    }

    /// <summary>A record as one line of at most <see cref="ItemLength"/> characters.</summary>
    public static string Shorten(object value)
    {
        string text = value as string ?? JsonSerializer.Serialize(value, Json);
        text = text.ReplaceLineEndings(" ");

        return text.Length <= ItemLength ? text : text[..ItemLength] + " …";
    }
}
