using System.Text.Json;
using AltiumWorkspaceMCP.Tools;
using ModelContextProtocol;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>
/// General response-size rule for writes: the summary in full, details — the first ones
/// that fit, the rest as a count "N more" (<c>resultsOmitted</c>).
/// </summary>
public sealed class ResponseFitTests
{
    private const int Max = 20000;

    private static string Serialize(object value) => JsonSerializer.Serialize(value, McpJsonUtilities.DefaultOptions);

    private static object Rows(int count, int width = 120) => Enumerable.Range(1, count)
        .Select(number => (object)new { component = $"CMP-{number:000}", text = new string('x', width) })
        .ToList();

    [Fact]
    public void ResponseWithinLimitIsReturnedAsIs()
    {
        string json = Serialize(new { applied = 3, results = Rows(3) });

        Assert.Same(json, ResponseFit.Fit(json, Max));
    }

    [Fact]
    public void NotAnObjectCannotBeFitted()
    {
        Assert.Null(ResponseFit.Fit(new string('x', 30000), Max));
        Assert.Null(ResponseFit.Fit(Serialize(Enumerable.Range(0, 5000).Select(number => $"row {number}").ToList()), Max));
    }

    [Fact]
    public void SummaryIsKeptAndDetailsAreCutWithACount()
    {
        string json = Serialize(new { applied = 200, skipped = 7, failed = 0, results = Rows(200), note = "summary" });
        Assert.True(json.Length > Max);

        string fitted = ResponseFit.Fit(json, Max)!;

        Assert.True(fitted.Length <= Max, $"{fitted.Length} characters");

        JsonElement root = JsonDocument.Parse(fitted).RootElement;
        Assert.Equal(200, root.GetProperty("applied").GetInt32());
        Assert.Equal(7, root.GetProperty("skipped").GetInt32());
        Assert.Equal("summary", root.GetProperty("note").GetString());

        int shown = root.GetProperty("results").GetArrayLength();
        Assert.InRange(shown, 1, 199);
        Assert.Equal(200 - shown, root.GetProperty("resultsOmitted").GetInt32());

        // Exactly the first ones are shown.
        Assert.Equal("CMP-001", root.GetProperty("results")[0].GetProperty("component").GetString());
        Assert.Contains("results — showing", root.GetProperty("responseTrimmed").GetString());
    }

    [Fact]
    public void OmissionAlreadyReportedByTheToolIsAddedUp()
    {
        // The tool itself showed 100 of 150 and wrote resultsOmitted = 50.
        string json = Serialize(new { applied = 150, results = Rows(100, 300), resultsOmitted = 50 });
        Assert.True(json.Length > Max);

        JsonElement root = JsonDocument.Parse(ResponseFit.Fit(json, Max)!).RootElement;

        int shown = root.GetProperty("results").GetArrayLength();
        Assert.Equal(150 - shown, root.GetProperty("resultsOmitted").GetInt32());
        Assert.Contains($"showing {shown} of 150", root.GetProperty("responseTrimmed").GetString());
    }

    [Fact]
    public void TextOmissionNoteIsKeptAndExtended()
    {
        string json = Serialize(new { read = Rows(200, 300), readOmitted = "3 more read operations." });

        JsonElement root = JsonDocument.Parse(ResponseFit.Fit(json, Max)!).RootElement;

        string omitted = root.GetProperty("readOmitted").GetString()!;
        Assert.StartsWith("3 more read operations.", omitted);
        Assert.Contains("shortened to the response limit", omitted);
    }

    [Fact]
    public void ShortListsStayWholeAndLongOnesGiveWay()
    {
        string json = Serialize(new
        {
            applied = 300,
            results = Rows(300),
            failures = Rows(4),
            skippedComponents = Rows(6),
        });

        JsonElement root = JsonDocument.Parse(ResponseFit.Fit(json, Max)!).RootElement;

        Assert.Equal(4, root.GetProperty("failures").GetArrayLength());
        Assert.Equal(6, root.GetProperty("skippedComponents").GetArrayLength());
        Assert.False(root.TryGetProperty("failuresOmitted", out _));
        Assert.True(root.GetProperty("results").GetArrayLength() < 300);
    }

    [Fact]
    public void GroupsAreKeptWholeWhileTheyTakeNoMoreThanHalf()
    {
        var groups = Enumerable.Range(1, 12)
            .Select(number => (object)$"Group {number}: 17 parts → Footprints: PCC-000-0527 (primary), PCC-000-0528 (#1), PCC-0014 (#2)")
            .ToList();

        string json = Serialize(new { applied = 200, groups, results = Rows(200, 200) });

        JsonElement root = JsonDocument.Parse(ResponseFit.Fit(json, Max)!).RootElement;

        Assert.Equal(12, root.GetProperty("groups").GetArrayLength());
        Assert.False(root.TryGetProperty("groupsOmitted", out _));
    }

    [Fact]
    public void ListsInsideRecordsAreCutToTheFirstFew()
    {
        var results = Enumerable.Range(1, 50)
            .Select(number => (object)new
            {
                component = $"CMP-{number:000}",
                corrections = Enumerable.Range(1, 25).Select(index => $"P{index}: number added 1E+3").ToList(),
            })
            .ToList();

        string json = Serialize(new { applied = 50, results });
        Assert.True(json.Length > Max);

        string fitted = ResponseFit.Fit(json, Max)!;
        Assert.True(fitted.Length <= Max, $"{fitted.Length} characters");

        JsonElement root = JsonDocument.Parse(fitted).RootElement;

        // All 50 parts are in place, but each has the first five corrections and a counter of the rest.
        Assert.Equal(50, root.GetProperty("results").GetArrayLength());
        JsonElement first = root.GetProperty("results")[0];
        Assert.Equal(ResponseFit.NestedLimit, first.GetProperty("corrections").GetArrayLength());
        Assert.Equal(25 - ResponseFit.NestedLimit, first.GetProperty("correctionsOmitted").GetInt32());
        Assert.Contains("corrections", root.GetProperty("responseTrimmed").GetString());
    }

    [Theory]
    [InlineData(20000)]
    [InlineData(8000)]
    [InlineData(3000)]
    public void FittedResponseNeverExceedsTheLimit(int max)
    {
        var results = Enumerable.Range(1, 400)
            .Select(number => (object)new
            {
                component = $"CMP-{number:000}",
                text = new string('x', 10 + number % 90),
                corrections = Enumerable.Range(1, number % 9).Select(index => new string('y', 60 + index)).ToList(),
            })
            .ToList();

        string json = Serialize(new
        {
            applied = 400,
            results,
            failures = Rows(30, 150),
            groups = Rows(10, 80),
            nested = new { batch = new { results = Rows(100, 100) } },
        });

        string? fitted = ResponseFit.Fit(json, max);

        Assert.NotNull(fitted);
        Assert.True(fitted!.Length <= max, $"{fitted.Length} characters at limit {max}");
        Assert.Equal(400, JsonDocument.Parse(fitted).RootElement.GetProperty("applied").GetInt32());
    }

    [Fact]
    public void ListsOfNestedObjectsAreFoundToo()
    {
        // A dry run puts the tool response in result, and with a remainder — in batch too.
        string json = Serialize(new { dryRun = true, result = new { batch = new { applied = 200, results = Rows(200) } } });

        JsonElement batch = JsonDocument.Parse(ResponseFit.Fit(json, Max)!).RootElement
            .GetProperty("result").GetProperty("batch");

        Assert.Equal(200, batch.GetProperty("applied").GetInt32());
        Assert.Equal(200, batch.GetProperty("results").GetArrayLength() + batch.GetProperty("resultsOmitted").GetInt32());
    }

    [Fact]
    public void SummaryThatDoesNotFitByItselfIsNotFitted()
    {
        string json = Serialize(new { note = new string('x', 30000), results = Rows(3) });

        Assert.Null(ResponseFit.Fit(json, Max));
    }
}
