using System.Text.Json;
using AltiumWorkspaceMCP.Soap.Vault;
using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>
/// A dry run report fits the response limit at any volume: reads and
/// writes are folded by operation, each section is limited by its share of the budget,
/// <c>result</c> goes without shortening.
/// </summary>
public sealed class DryRunTests
{
    private static ALU_ItemRevision Revision(string hrid, string revisionId = "01", int parameterCount = 0)
    {
        var parameters = new _ALU_ItemRevisionParameterList();
        for (int i = 0; i < parameterCount; i++)
        {
            parameters.Add(new ALU_ItemRevisionParameter
            {
                HRID = $"Param{i}",
                ParameterValue = new string('x', 200),
                ParameterTypeGUID = string.Empty,
            });
        }

        return new ALU_ItemRevision
        {
            GUID = Guid.NewGuid().ToString(),
            ItemHRID = hrid,
            RevisionId = revisionId,
            FolderGUID = "FOLDER-1",
            RevisionParameters = parameters,
        };
    }

    [Fact]
    public async Task ManyWriteCallsOfSameOperationCollapseToOneEntryWithCallCountAndTotal()
    {
        var recorder = await CaptureAsync(() =>
        {
            // Like ComponentCopyService: one write operation per copy, not one batch.
            for (int i = 1; i <= 20; i++)
            {
                DryRun.Intercept("ExecuteScript: Release single component", new[] { Revision($"CMP-{i:000}") });
            }

            return Task.FromResult<object>(new { });
        });

        object report = recorder.Report(new { });
        JsonElement json = ToJson(report);

        JsonElement wouldWrite = json.GetProperty("wouldWrite");
        Assert.Equal(1, wouldWrite.GetArrayLength());

        JsonElement entry = wouldWrite[0];
        Assert.Equal("ExecuteScript: Release single component", entry.GetProperty("operation").GetString());
        Assert.Equal(20, entry.GetProperty("calls").GetInt32());
        Assert.Equal(20, entry.GetProperty("records").GetInt64());

        // The sample is only on the first record, not on all twenty.
        Assert.Equal(1, entry.GetProperty("sample").GetArrayLength());
    }

    [Fact]
    public async Task ManyReadCallsOfSameOperationCollapseToOneEntry()
    {
        var recorder = await CaptureAsync(() =>
        {
            for (int i = 1; i <= 40; i++)
            {
                DryRun.NoteRead("GetALU_ItemRevisions", $"GUID = 'R{i}'", new[] { Revision($"CMP-{i:000}", parameterCount: 30) });
            }

            return Task.FromResult<object>(new { });
        });

        object report = recorder.Report(new { });
        JsonElement json = ToJson(report);

        JsonElement read = json.GetProperty("read");
        Assert.Equal(1, read.GetArrayLength());

        JsonElement entry = read[0];
        Assert.Equal("GetALU_ItemRevisions", entry.GetProperty("operation").GetString());
        Assert.Equal(40, entry.GetProperty("calls").GetInt32());
        Assert.Equal(40, entry.GetProperty("records").GetInt64());
    }

    [Fact]
    public async Task ReadSampleIsBriefWithoutFullParameterList()
    {
        var recorder = await CaptureAsync(() =>
        {
            DryRun.NoteRead("GetALU_ItemRevisions", null, new[] { Revision("CMP-001", "03", parameterCount: 30) });
            return Task.FromResult<object>(new { });
        });

        JsonElement entry = ToJson(recorder.Report(new { })).GetProperty("read")[0];
        JsonElement sample = entry.GetProperty("sample")[0];

        Assert.Equal("CMP-001", sample.GetProperty("item").GetString());
        Assert.Equal("03", sample.GetProperty("revision").GetString());
        Assert.Equal("FOLDER-1", sample.GetProperty("folder").GetString());

        // There is no full parameter list in the short sample.
        Assert.False(sample.TryGetProperty("parameters", out _));
    }

    [Fact]
    public async Task ReadsAndWritesBeyondBudgetAreReplacedByCounts()
    {
        var recorder = await CaptureAsync(() =>
        {
            // Many different operations (not repeats of one) — each creates its own report row,
            // and together they do not fit the section budget.
            for (int op = 1; op <= 150; op++)
            {
                DryRun.NoteRead($"GetOperation{op}", null, new[] { Revision($"CMP-{op:000}", parameterCount: 30) });
                DryRun.Intercept($"WriteOperation{op}", new[] { Revision($"CMP-{op:000}", parameterCount: 30) });
            }

            return Task.FromResult<object>(new { });
        });

        object report = recorder.Report(new { });
        JsonElement json = ToJson(report);

        int readChars = json.GetProperty("read").GetRawText().Length;
        int writeChars = json.GetProperty("wouldWrite").GetRawText().Length;

        // The budget limits the sum over operations, but the first record of each section is taken
        // always — the tolerance covers its own size above the budget.
        Assert.True(readChars <= DryRunRecorder.ReadBudgetChars + 3000, $"read: {readChars} characters");
        Assert.True(writeChars <= DryRunRecorder.WriteBudgetChars + 3000, $"wouldWrite: {writeChars} characters");

        Assert.Contains("read operations", json.GetProperty("readOmitted").GetString());
        Assert.Contains("write operations", json.GetProperty("wouldWriteOmitted").GetString());

        // The whole report fits the tool response limit.
        Assert.True(json.GetRawText().Length < 20000, $"total: {json.GetRawText().Length} characters");
    }

    [Fact]
    public async Task ResultIsNeverTrimmed()
    {
        var recorder = await CaptureAsync(() => Task.FromResult<object>(new { }));

        string bigValue = new('x', 30000);
        object report = recorder.Report(new { data = bigValue });

        JsonElement json = ToJson(report);
        Assert.Equal(bigValue, json.GetProperty("result").GetProperty("data").GetString());
    }

    [Fact]
    public async Task NoOmissionFieldWhenEverythingFits()
    {
        var recorder = await CaptureAsync(() =>
        {
            DryRun.NoteRead("GetALU_Items", null, new[] { Revision("CMP-001") });
            DryRun.Intercept("write", new[] { Revision("CMP-001") });
            return Task.FromResult<object>(new { });
        });

        JsonElement json = ToJson(recorder.Report(new { }));

        Assert.Equal(JsonValueKind.Null, json.GetProperty("readOmitted").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("wouldWriteOmitted").ValueKind);
    }

    private static async Task<DryRunRecorder> CaptureAsync(Func<Task<object>> body)
    {
        (object _, DryRunRecorder recorder) = await DryRun.CaptureAsync("test", body);
        return recorder;
    }

    private static JsonElement ToJson(object value) => JsonSerializer.SerializeToElement(value);
}
