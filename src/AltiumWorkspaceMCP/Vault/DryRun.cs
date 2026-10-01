using AltiumWorkspaceMCP.Soap.Vault;
using AltiumWorkspaceMCP.Tools;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>What was read from the server during a dry run.</summary>
public sealed record DryRunRead(string Operation, string? Filter, int Count, IReadOnlyList<object>? Sample);

/// <summary>What would have been sent to the server if the run were real.</summary>
public sealed record DryRunWrite(string Operation, int Count, IReadOnlyList<object> Sample, string? Note);

/// <summary>
/// Log of one dry run: all reads and all changes that would be sent.
/// </summary>
public sealed class DryRunRecorder
{
    /// <summary>How many records of each change go into the report in full.</summary>
    public const int WriteSampleSize = 25;

    /// <summary>How many records of each read go into the report: reads are numerous and large.</summary>
    public const int ReadSampleSize = 3;

    /// <summary>A long filter (GUID lists) is truncated in the report.</summary>
    private const int FilterLength = 300;

    private readonly Lock _gate = new();
    private readonly List<DryRunRead> _reads = [];
    private readonly List<DryRunWrite> _writes = [];

    public DryRunRecorder(string tool) => Tool = tool;

    public string Tool { get; }

    public IReadOnlyList<DryRunRead> Reads
    {
        get
        {
            lock (_gate)
            {
                return _reads.ToList();
            }
        }
    }

    public IReadOnlyList<DryRunWrite> Writes
    {
        get
        {
            lock (_gate)
            {
                return _writes.ToList();
            }
        }
    }

    /// <summary>How many characters the samples of one read take: reads are large, and the report is limited by the client response.</summary>
    public const int ReadSampleChars = 1500;

    /// <summary>
    /// How many characters the samples of one change take (not counting the record counter).
    /// One edit call is one log entry regardless of how many parts it
    /// affected (200 parts in one vault_set_links call is also one entry), so
    /// the read/wouldWrite section does not have to fold calls by operation: there are few of them anyway.
    /// The number limits the sample inside one call so that together with result (which has its own
    /// limit by record count, not characters) it fits the response limit.
    /// </summary>
    public const int WriteSampleChars = 3500;

    /// <summary>Takes samples while their total JSON length fits the limit; the first is always taken.</summary>
    private static List<object> TakeSamples(IEnumerable<object> views, int maxItems, int maxChars)
    {
        var taken = new List<object>();
        int used = 0;

        foreach (object view in views.Take(maxItems))
        {
            int size = System.Text.Json.JsonSerializer.Serialize(view, Tools.ResponseJson.Options).Length;

            if (taken.Count > 0 && used + size > maxChars)
            {
                break;
            }

            used += size;
            taken.Add(view);
        }

        return taken;
    }

    public void RecordRead<T>(string operation, string? filter, IReadOnlyCollection<T> records)
    {
        var sample = TakeSamples(
            records.Select(record => DryRunView.Brief(record)).Where(view => view is not null).Select(view => view!),
            ReadSampleSize,
            ReadSampleChars);

        string? shownFilter = string.IsNullOrEmpty(filter)
            ? null
            : filter.Length <= FilterLength ? filter : filter[..FilterLength] + " …";

        lock (_gate)
        {
            _reads.Add(new DryRunRead(operation, shownFilter, records.Count, sample.Count == 0 ? null : sample));
        }
    }

    public void RecordWrite<T>(string operation, IReadOnlyCollection<T> records, string? note = null)
    {
        var sample = TakeSamples(
            records.Select(record => DryRunView.Of(record) ?? (object)record!),
            WriteSampleSize,
            WriteSampleChars);

        lock (_gate)
        {
            _writes.Add(new DryRunWrite(operation, records.Count, sample, note));
        }
    }

    /// <summary>
    /// Share of the response budget for the <c>read</c> section of the run report. Less than half of the limit:
    /// the other half is shared by the <c>wouldWrite</c> section and the untouched <c>result</c>
    /// (which has its own limit by record count, not characters — with Cyrillic that is more characters
    /// than it seems by eye).
    /// </summary>
    public const int ReadBudgetChars = 2000;

    /// <summary>Share of the response budget for the <c>wouldWrite</c> section of the run report.</summary>
    public const int WriteBudgetChars = 2000;

    /// <summary>
    /// The run report together with what the tool would return on a real execution.
    /// </summary>
    /// <remarks>
    /// <c>result</c> — what the run is called for — is not cut here; the general size limit
    /// (<see cref="ResponseFit"/>) is applied by <c>ResponseSizeGuard</c> to the finished response and
    /// shortens only the per-part lists in it. The <c>read</c> and
    /// <c>wouldWrite</c> sections are folded by operation (one parameter-edit call for 200 parts
    /// or 200 separate copy-creation calls is one line with a call and record count,
    /// the sample is taken only from the first), and each section fits its share of the response
    /// budget: what did not fit is replaced by one line with a counter.
    /// </remarks>
    public object Report(object result)
    {
        (var read, string? readOmitted) = SummarizeReads();
        (var wouldWrite, string? writeOmitted) = SummarizeWrites();

        return new
        {
            dryRun = true,
            note = "Dry run: everything was really read from the server, the changes were collected, "
                + "but not sent to the server. Repeat the call without dryRun to apply.",
            result,
            read,
            readOmitted,
            wouldWrite,
            wouldWriteOmitted = writeOmitted,
        };
    }

    /// <summary>Journal reads — one line per operation, within the budget.</summary>
    private (IReadOnlyList<object> Items, string? Omitted) SummarizeReads()
    {
        var groups = Reads
            .GroupBy(read => read.Operation, StringComparer.Ordinal)
            .Select(group => (
                Operation: group.Key,
                Calls: group.Count(),
                Records: group.Sum(read => (long)read.Count),
                Filter: group.First().Filter,
                Sample: group.First().Sample))
            .ToList();

        var budget = new ResponseBudget(ReadBudgetChars);
        var items = new List<object>();
        int omittedOperations = 0;
        long omittedRecords = 0;

        foreach (var group in groups)
        {
            object entry = new
            {
                operation = group.Operation,
                calls = group.Calls,
                records = group.Records,
                filter = group.Filter,
                sample = group.Sample,
            };

            if (budget.TryAdd(entry))
            {
                items.Add(entry);
            }
            else
            {
                omittedOperations++;
                omittedRecords += group.Records;
            }
        }

        return (items, Omitted(omittedOperations, omittedRecords, "read"));
    }

    /// <summary>Journal writes — one line per operation, within the budget.</summary>
    private (IReadOnlyList<object> Items, string? Omitted) SummarizeWrites()
    {
        var groups = Writes
            .GroupBy(write => write.Operation, StringComparer.Ordinal)
            .Select(group => (
                Operation: group.Key,
                Calls: group.Count(),
                Records: group.Sum(write => (long)write.Count),
                Note: group.First().Note,
                Sample: group.First().Sample))
            .ToList();

        var budget = new ResponseBudget(WriteBudgetChars);
        var items = new List<object>();
        int omittedOperations = 0;
        long omittedRecords = 0;

        foreach (var group in groups)
        {
            object entry = new
            {
                operation = group.Operation,
                calls = group.Calls,
                records = group.Records,
                note = group.Note,
                sample = group.Sample,
            };

            if (budget.TryAdd(entry))
            {
                items.Add(entry);
            }
            else
            {
                omittedOperations++;
                omittedRecords += group.Records;
            }
        }

        return (items, Omitted(omittedOperations, omittedRecords, "write"));
    }

    private static string? Omitted(int operations, long records, string noun) => operations == 0
        ? null
        : $"{operations} more {noun} operations, {records} records — did not fit the response budget.";
}

/// <summary>
/// Dry run: everything is read for real, changes are collected, but not sent to the server
/// (nothing is written).
/// </summary>
/// <remarks>
/// The mode propagates along the async call flow and is intercepted at the very bottom —
/// in the service gateway and in the script executor, where the data would go to the server. All the logic
/// above runs the same as in a real write, so the run honestly
/// shows what would be written: only the last step differs.
/// </remarks>
public static class DryRun
{
    private static readonly AsyncLocal<DryRunRecorder?> Scope = new();

    public static DryRunRecorder? Current => Scope.Value;

    public static bool IsActive => Scope.Value is not null;

    /// <summary>
    /// Runs a tool body: with <paramref name="dryRun"/> — as a dry run
    /// with a report of what was read and the write that did not happen, otherwise — as usual.
    /// </summary>
    public static async Task<object> RunAsync(string tool, bool dryRun, Func<Task<object>> body)
    {
        if (!dryRun || IsActive)
        {
            return await body();
        }

        DryRunRecorder? previous = Scope.Value;
        var recorder = new DryRunRecorder(tool);
        Scope.Value = recorder;

        try
        {
            object result = await body();
            return recorder.Report(result);
        }
        finally
        {
            Scope.Value = previous;
        }
    }

    /// <summary>
    /// Runs the body as a dry run and returns what it returned and the write journal.
    /// Needed by the confirmation preview: the journal is compressed into a short list of changes.
    /// </summary>
    public static async Task<(object Result, DryRunRecorder Recorder)> CaptureAsync(
        string tool,
        Func<Task<object>> body)
    {
        DryRunRecorder? previous = Scope.Value;
        var recorder = new DryRunRecorder(tool);
        Scope.Value = recorder;

        try
        {
            object result = await body();
            return (result, recorder);
        }
        finally
        {
            Scope.Value = previous;
        }
    }

    /// <summary>Records a change in the journal if the run is a dry run; returns whether it is a dry run.</summary>
    public static bool Intercept<T>(string operation, IReadOnlyCollection<T> records, string? note = null)
    {
        DryRunRecorder? recorder = Scope.Value;
        if (recorder is null)
        {
            return false;
        }

        recorder.RecordWrite(operation, records, note);
        return true;
    }

    /// <summary>Marks a read in the journal of a dry run if one is in progress.</summary>
    public static void NoteRead<T>(string operation, string? filter, IReadOnlyCollection<T> records) =>
        Scope.Value?.RecordRead(operation, filter, records);
}

/// <summary>
/// Compact representation of vault objects for the report: only what is needed to
/// check the data by eye, without the proxy service fields.
/// </summary>
public static class DryRunView
{
    /// <summary>
    /// Short form of a record for the <c>read</c> section of the run report: only what identifies the part
    /// by eye (hrid, revision, folder), without the full list of parameters and links —
    /// otherwise the records read alone would fill the whole response budget.
    /// </summary>
    public static object? Brief(object? record) => record switch
    {
        ALU_ItemRevision revision => new
        {
            item = revision.ItemHRID,
            revision = revision.RevisionId,
            folder = revision.FolderGUID,
        },
        ALU_Item item => new { hrid = item.HRID, folder = item.FolderGUID },
        ALU_ItemRevisionParameter parameter => $"{parameter.HRID} = {parameter.ParameterValue}",
        _ => Of(record),
    };

    public static object? Of(object? record) => record switch
    {
        ALU_ItemRevision revision => Revision(revision),
        ALU_ItemRevisionLink link => Link(link),
        ALU_Item item => Item(item),
        ALU_ItemRevisionParameter parameter => Parameter(parameter),
        ALU_MoveItem move => new { item = move.GUID, folder = move.FolderGUID },
        ALU_MoveFolder move => new { folder = move.GUID, parent = move.ParentFolderGUID },
        ALU_Folder folder => new
        {
            guid = folder.GUID,
            name = folder.HRID,
            parent = folder.ParentFolderGUID,
            description = folder.Description,
            type = folder.FolderTypeGUID,
            attributes = folder.Attributes,
            parameters = folder.FolderParameters?.Select(parameter => $"{parameter.HRID} = {parameter.DefaultValue}").ToList(),
        },
        ALU_Tag tag => new { guid = tag.GUID, name = tag.HRID, parent = tag.ParentTagGUID },
        ALU_ItemTag itemTag => new { item = itemTag.ItemGUID, tag = itemTag.TagGUID },
        string text => text,
        _ => null,
    };

    public static object Item(ALU_Item item)
    {
        ALU_ItemRevision? latest = ComponentService.SelectLatestRevision(item);

        return new
        {
            guid = item.GUID,
            hrid = item.HRID,
            folder = item.FolderGUID,
            revisions = item.Revisions?.Count ?? 0,
            latestRevision = latest is null ? null : Revision(latest),
        };
    }

    /// <summary>
    /// How many revision parameters go into the report sample in full: a part may have
    /// dozens, and one such sample in a call with hundreds of parts (one journal entry for the whole
    /// batch, not per part) can by itself fill the whole response budget.
    /// </summary>
    private const int ParameterSampleSize = 20;

    public static object Revision(ALU_ItemRevision revision) => new
    {
        guid = revision.GUID,
        item = revision.ItemHRID,
        revision = revision.RevisionId,
        ancestor = string.IsNullOrEmpty(revision.AncestorItemRevisionGUID) ? null : revision.AncestorItemRevisionGUID,
        released = RevisionService.IsReleased(revision),
        comment = revision.Comment,
        description = revision.Description,
        parameters = ParameterSample(revision.RevisionParameters),
    };

    /// <summary>The first <see cref="ParameterSampleSize"/> parameters; the extra ones are replaced by a counter.</summary>
    private static IReadOnlyList<string>? ParameterSample(_ALU_ItemRevisionParameterList? parameters)
    {
        if (parameters is null || parameters.Count == 0)
        {
            return null;
        }

        var shown = parameters.Take(ParameterSampleSize).Select(Parameter).ToList();

        if (parameters.Count > ParameterSampleSize)
        {
            shown.Add($"…{parameters.Count - ParameterSampleSize} more parameters");
        }

        return shown;
    }

    public static object Link(ALU_ItemRevisionLink link) => new
    {
        guid = link.GUID,
        role = link.HRID,
        parent = link.ParentItemRevisionGUID,
        child = link.ChildItemRevisionGUID,
        data = string.IsNullOrEmpty(link.Data) ? null : link.Data,
        parentVault = string.IsNullOrEmpty(link.ParentVaultGUID) ? "none" : link.ParentVaultGUID,
        childVault = string.IsNullOrEmpty(link.ChildVaultGUID) ? "none" : link.ChildVaultGUID,
    };

    /// <summary>
    /// Parameter as one line: "Max Operating Temperature = 70°C [Temperature 7.00000000000000E+0001]".
    /// For a typed parameter without a number this is visible at once — Altium clears exactly those.
    /// </summary>
    public static string Parameter(ALU_ItemRevisionParameter parameter)
    {
        string type = ParameterValueCodec.DescribeType(parameter.ParameterTypeGUID);

        string number = ParameterValueCodec.IsText(parameter.ParameterTypeGUID)
            ? string.Empty
            : parameter.ParameterRealValue is null
                ? " no number (null)"
                : parameter.ParameterRealValue.Length == 0
                    ? " number is empty"
                    : " " + parameter.ParameterRealValue;

        return $"{parameter.HRID} = {parameter.ParameterValue} [{type}{number}]";
    }
}
