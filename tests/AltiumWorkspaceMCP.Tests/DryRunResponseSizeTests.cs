using System.Text.Json;
using AltiumWorkspaceMCP.Mcp;
using AltiumWorkspaceMCP.Soap.Vault;
using AltiumWorkspaceMCP.Tools;
using AltiumWorkspaceMCP.Vault;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit.Abstractions;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>
/// A dry run and a real write on 50 and 200 parts fit the response limit together with the
/// summary: the responses are built by the same functions as in the tools, the report by the same
/// <see cref="DryRunRecorder"/>, and the limit is applied by the same <see cref="ResponseSizeGuard"/>.
/// </summary>
public sealed class DryRunResponseSizeTests(ITestOutputHelper output)
{
    private const int Max = 20000;

    private static string Hrid(int number) => $"CMP-000-{number:0000}";

    private static ALU_ItemRevision Revision(int number, int parameterCount = 47)
    {
        var parameters = new _ALU_ItemRevisionParameterList();

        for (int index = 0; index < parameterCount; index++)
        {
            parameters.Add(new ALU_ItemRevisionParameter
            {
                HRID = $"Parameter {index}",
                ParameterValue = $"value {index} " + new string('x', 20),
                ParameterRealValue = "1.00000000000000E+0003",
                ParameterTypeGUID = string.Empty,
            });
        }

        return new ALU_ItemRevision
        {
            GUID = Guid.NewGuid().ToString(),
            ItemHRID = Hrid(number),
            RevisionId = "02",
            FolderGUID = Guid.NewGuid().ToString(),
            Comment = "Resistor 0805 1% 1/8W",
            Description = "Resistor",
            RevisionParameters = parameters,
        };
    }

    private static ALU_ItemRevisionLink Link(string role, string target) => new()
    {
        GUID = Guid.NewGuid().ToString(),
        HRID = role,
        ParentItemRevisionGUID = Guid.NewGuid().ToString(),
        ChildItemRevisionGUID = target,
        ParentVaultGUID = Guid.NewGuid().ToString(),
        ChildVaultGUID = Guid.NewGuid().ToString(),
        Data = "FootprintIndex=0;IsDefaultFootprint=true;ModelName=R_0805;Description=Resistor 0805",
    };

    /// <summary>What a batch of <paramref name="count"/> revisions would write: one journal entry per batch (as ReleaseRevisionsAsync).</summary>
    private static void RecordRelease(int count, bool footprintLinks)
    {
        DryRun.Intercept(
            "ExecuteScript: ReleaseALU_ItemRevisions",
            Enumerable.Range(1, count).Select(number => (object)new
            {
                createdByScript = true,
                revision = DryRunView.Revision(Revision(number)),
                links = new[]
                {
                    Link("Symbol", "sym"),
                    Link("PCBLIB", "fp0"),
                    Link(footprintLinks ? "PCBLIB 1" : "Template", "fp1"),
                    Link(footprintLinks ? "PCBLIB 2" : "Datasheet", "fp2"),
                }.Select(DryRunView.Link).ToList(),
            }).ToList(),
            $"create revisions: {count}, release: {count}, note 'test'");
    }

    private static void RecordReads(int count)
    {
        DryRun.NoteRead("GetALU_Items", "HRID IN (...)", Enumerable.Range(1, count).Select(number => Revision(number)).ToList());
        DryRun.NoteRead("GetALU_ItemRevisionLinks", "ParentItemRevisionGUID IN (...)", Enumerable.Range(1, count * 4).Select(number => Link("PCBLIB", "t")).ToList());
        DryRun.NoteRead("SearchALU_Items", "MPN", Enumerable.Range(1, 5).Select(number => Revision(number)).ToList());
    }

    private static List<RevisionChangeResult> Applied(int count, IReadOnlyList<string> corrections) => Enumerable.Range(1, count)
        .Select(number => new RevisionChangeResult(
            Guid.NewGuid().ToString(), Hrid(number), "02", "03", Guid.NewGuid().ToString(), true, 2, 6,
            ["PCBLIB → PCC-000-0527", "PCBLIB 1 → PCC-000-0528", "PCBLIB 2 → PCC-0014"])
        {
            Corrections = corrections,
        })
        .ToList();

    /// <summary>Corrections that Altium appends to typed parameters: one line per parameter.</summary>
    private static List<string> NumberCorrections(int count) => Enumerable.Range(1, count)
        .Select(index => $"'Parameter {index}' = 'value {index}': number 1.00000000000000E+0003 appended")
        .ToList();

    private static readonly string[] FootprintCorrections =
    [
        "footprint: data assigned FootprintIndex=0, IsDefaultFootprint=true; role 'PCBLIB' → 'PCBLIB'",
        "footprint: data assigned FootprintIndex=1, IsDefaultFootprint=false; role 'PCBLIB 1' → 'PCBLIB 1'",
        "footprint: data assigned FootprintIndex=2, IsDefaultFootprint=false; role 'PCBLIB 2' → 'PCBLIB 2'",
        "Symbol: vault GUID set",
    ];

    private static JsonElement Parse(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);

        string text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True(text.Length <= Max, $"{text.Length} characters at limit {Max}");

        return JsonDocument.Parse(text).RootElement;
    }

    // ── vault_update_parameters ─────────────────────────────────────────────────

    [Theory]
    [InlineData(50)]
    [InlineData(200)]
    public async Task UpdateParametersDryRunFitsAndKeepsTheSummary(int count)
    {
        CallToolResult response = await RespondAsync(
            "vault_update_parameters",
            () => EditingTools.ApplyResponse(
                new BatchResult(Applied(count, NumberCorrections(8)), [("CMP-000-9999", "value not parsed")]),
                [new { component = "CMP-000-9999", error = "value not parsed" }],
                Enumerable.Range(1, 40).Select(number => (object)new { component = Hrid(number), errors = new[] { "Parameter 'X': 'abc' is not a number" } }).ToList(),
                Enumerable.Range(1, 30).Select(number => (object)new
                {
                    copy = (int?)null,
                    component = Hrid(number),
                    parameter = "Manufacturer Part Number",
                    value = "RC0805FR-071KL",
                    existing = new[] { "CMP-000-0001", "CMP-000-0002" },
                }).ToList(),
                Enumerable.Range(1, 12).Select(number => (object)new { component = Hrid(number), reason = "values already match" }).ToList(),
                null),
            count);

        JsonElement root = Parse(response);
        JsonElement result = root.GetProperty("result");

        Assert.True(root.GetProperty("dryRun").GetBoolean());
        Assert.Equal(count, result.GetProperty("applied").GetInt32());
        Assert.Equal(12, result.GetProperty("skipped").GetInt32());
        Assert.Equal(1, result.GetProperty("failed").GetInt32());

        // Summary and "N more": what is shown and what is omitted together give all the parts.
        int shown = result.GetProperty("results").GetArrayLength();
        int omitted = result.TryGetProperty("resultsOmitted", out JsonElement value) ? value.GetInt32() : 0;
        Assert.Equal(count, shown + omitted);
        Assert.True(shown >= 3, $"parts shown: {shown}");
    }

    [Fact]
    public async Task RealWriteOfFiftyPartsFitsToo()
    {
        // Without dryRun: the same response, without the report; the write result fits the limit too.
        CallToolResult response = await RespondAsync(
            "vault_update_parameters",
            () => EditingTools.ApplyResponse(
                new BatchResult(Applied(50, NumberCorrections(8)), []),
                [], null, null, [], null),
            50,
            dryRun: false);

        JsonElement root = Parse(response);

        Assert.Equal(50, root.GetProperty("applied").GetInt32());
        Assert.Equal(0, root.GetProperty("skipped").GetInt32());
        Assert.Contains("Response shortened", root.GetProperty("responseTrimmed").GetString());
    }

    [Fact]
    public async Task UpdateParametersOfFiftyPartsWithoutTheFitWouldBeRejected()
    {
        // Test safeguard: the original response really did not fit (in the live measurement — 91 107 characters).
        string raw = await RawAsync(
            () => EditingTools.ApplyResponse(new BatchResult(Applied(50, NumberCorrections(8)), []), [], null, null, [], null),
            50);

        Assert.True(raw.Length > Max, $"{raw.Length} characters");
    }

    // ── vault_table_write ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(50)]
    [InlineData(200)]
    public async Task TableWriteDryRunFitsWithTheRemainderList(int count)
    {
        // The table is larger than MaxWriteBatch: the response is {batch, remaining, remainingComponents}.
        int remaining = count * 3;

        CallToolResult response = await RespondAsync(
            "vault_table_write",
            () => new
            {
                batch = EditingTools.ApplyResponse(
                    new BatchResult(Applied(count, NumberCorrections(8)), []), [], null, null, [], null),
                remaining,
                note = $"At most {count} components are changed per call. {remaining} left: repeat the call for the remaining rows.",
                remainingComponents = Enumerable.Range(1, remaining).Select(Hrid).ToList(),
            },
            count);

        JsonElement batch = Parse(response).GetProperty("result").GetProperty("batch");

        Assert.Equal(count, batch.GetProperty("applied").GetInt32());

        int shown = batch.GetProperty("results").GetArrayLength();
        int omitted = batch.TryGetProperty("resultsOmitted", out JsonElement value) ? value.GetInt32() : 0;
        Assert.Equal(count, shown + omitted);
        Assert.True(shown >= 3, $"parts shown: {shown}");
    }

    // ── vault_set_links ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(17)]
    [InlineData(50)]
    [InlineData(200)]
    public async Task SetLinksFootprintsDryRunFitsAndKeepsTheGroups(int count)
    {
        // Three targets in the footprint set, corrections on every part; three groups.
        string[] groups =
        [
            .. Enumerable.Range(1, 3).Select(number => $"Group {number}: {count / 3} parts → "
                + "Footprints: PCC-000-0527 — R 0805 Normal (primary), PCC-000-0528 — R 0805 Least (#1), PCC-0014 — R 0805 Most (#2)"),
        ];

        CallToolResult response = await RespondAsync(
            "vault_set_links",
            () => EditingTools.LinksResponse(
                new BatchResult(Applied(count, FootprintCorrections), []),
                Enumerable.Range(1, 9).Select(number => (object)new { component = Hrid(number), reason = "links already match and are written in the Altium format" }).ToList(),
                [],
                groups,
                1234,
                0,
                500,
                null),
            count,
            footprintLinks: true);

        JsonElement result = Parse(response).GetProperty("result");

        Assert.Equal(count, result.GetProperty("applied").GetInt32());
        Assert.Equal(9, result.GetProperty("skipped").GetInt32());
        Assert.Equal(0, result.GetProperty("failed").GetInt32());

        // Groups are part of the summary, in full.
        Assert.Equal(3, result.GetProperty("groups").GetArrayLength());

        int shown = result.GetProperty("results").GetArrayLength();
        int omitted = result.TryGetProperty("resultsOmitted", out JsonElement value) ? value.GetInt32() : 0;
        Assert.Equal(count, shown + omitted);
        Assert.True(shown >= 3, $"parts shown: {shown}");
    }

    private async Task<CallToolResult> RespondAsync(
        string tool,
        Func<object> result,
        int count,
        bool dryRun = true,
        bool footprintLinks = false)
    {
        object response;

        if (dryRun)
        {
            (object produced, DryRunRecorder recorder) = await DryRun.CaptureAsync(tool, () =>
            {
                RecordReads(count);
                RecordRelease(count, footprintLinks);
                return Task.FromResult(result());
            });

            response = recorder.Report(produced);
        }
        else
        {
            response = result();
        }

        var raw = new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(response, McpJsonUtilities.DefaultOptions) }],
        };

        CallToolResult fitted = ResponseSizeGuard.Limit(tool, readOnly: false, raw, Max, dryRun);
        output.WriteLine($"{tool}, {count} parts{(dryRun ? ", dryRun" : string.Empty)}: "
            + $"before the limit {ResponseSizeGuard.SizeOf(raw)} characters, after {ResponseSizeGuard.SizeOf(fitted)}; "
            + $"parts shown: {ShownResults(fitted)}");

        return fitted;
    }

    /// <summary>How many results records remain in the response (in a dry run they are in result, and with a remainder — in batch too).</summary>
    private static int ShownResults(CallToolResult response)
    {
        if (response.Content[0] is not TextContentBlock { Text: { } text })
        {
            return -1;
        }

        JsonElement node = JsonDocument.Parse(text).RootElement;

        foreach (string step in new[] { "result", "batch" })
        {
            if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty(step, out JsonElement inner))
            {
                node = inner;
            }
        }

        return node.ValueKind == JsonValueKind.Object && node.TryGetProperty("results", out JsonElement results)
            ? results.GetArrayLength()
            : -1;
    }

    private async Task<string> RawAsync(Func<object> result, int count)
    {
        (object produced, DryRunRecorder recorder) = await DryRun.CaptureAsync("raw", () =>
        {
            RecordReads(count);
            RecordRelease(count, footprintLinks: false);
            return Task.FromResult(result());
        });

        return JsonSerializer.Serialize(recorder.Report(produced), McpJsonUtilities.DefaultOptions);
    }
}
