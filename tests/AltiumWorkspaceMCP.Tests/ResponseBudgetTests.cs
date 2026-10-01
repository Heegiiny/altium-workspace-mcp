using System.Text.Json;
using AltiumWorkspaceMCP.Mcp;
using AltiumWorkspaceMCP.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>Response size budget.</summary>
public sealed class ResponseBudgetTests
{
    [Fact]
    public void SizeMatchesTheSdkSerializer()
    {
        object value = new { path = @"Components\Passive", text = "Resistor \"1k\"" };

        Assert.Equal(
            JsonSerializer.Serialize(value, ResponseJson.Options).Length,
            ResponseBudget.SizeOf(value));

        // A backslash is doubled in JSON — the estimate accounts for it.
        Assert.True(ResponseBudget.SizeOf(@"a\b") > 3 + 2);
    }

    // Russian part names reach the client as they are, not as \uXXXX sequences.
    // "Resistor" in Russian, written with escapes so that the source stays ASCII.
    private const string RussianName = "\u0420\u0435\u0437\u0438\u0441\u0442\u043e\u0440 0805";

    [Fact]
    public void ResponseSerializerKeepsRussianLettersUnescaped()
    {
        object value = new { name = RussianName };

        string json = JsonSerializer.Serialize(value, ResponseJson.Options);

        Assert.DoesNotContain("\\u", json);
        Assert.Contains(RussianName, json);
        Assert.Equal(json.Length, ResponseBudget.SizeOf(value));

        // The default SDK options would cost 6 characters per letter.
        Assert.True(JsonSerializer.Serialize(value, McpJsonUtilities.DefaultOptions).Length > json.Length);
    }

    [Fact]
    public void TakesRowsWhileTheyFit()
    {
        var rows = Enumerable.Range(0, 100).Select(number => (object)new[] { $"CMP-{number:000}", "10k" }).ToList();
        int one = ResponseBudget.SizeOf(rows[0]) + 1;

        var budget = new ResponseBudget(maxChars: one * 10 + 5);
        var taken = budget.TakeFitting(rows);

        Assert.Equal(10, taken.Count);
        Assert.True(budget.Used <= one * 10 + 5);
        Assert.True(budget.Remaining < one);
    }

    [Fact]
    public void ReservedSpaceIsNotAvailable()
    {
        var rows = Enumerable.Range(0, 50).Select(number => (object)$"row {number}").ToList();
        int one = ResponseBudget.SizeOf(rows[0]) + 1;

        var taken = new ResponseBudget(maxChars: one * 20, reserved: one * 10).TakeFitting(rows);

        Assert.Equal(10, taken.Count);
    }

    [Fact]
    public void FirstItemIsAlwaysTaken()
    {
        var budget = new ResponseBudget(maxChars: 5);

        Assert.True(budget.TryAdd(new string('x', 1000)));
        Assert.False(budget.TryAdd("more"));
    }

    private static CallToolResult Text(int length) =>
        new() { Content = [new TextContentBlock { Text = new string('x', length) }] };

    [Fact]
    public void ResultWithinLimitIsReturnedAsIs()
    {
        CallToolResult result = Text(1000);

        Assert.Same(result, ResponseSizeGuard.Limit("vault_table", readOnly: true, result, maxChars: 1000));
    }

    [Fact]
    public void OversizedReadResultBecomesAnErrorWithAdvice()
    {
        CallToolResult replaced = ResponseSizeGuard.Limit("vault_table", readOnly: true, Text(25000), maxChars: 20000);

        Assert.True(replaced.IsError);
        string message = Assert.IsType<TextContentBlock>(Assert.Single(replaced.Content)).Text;
        Assert.Contains("vault_table", message);
        Assert.Contains("25000", message);
        Assert.Contains("20000", message);
        Assert.Contains("limit", message);
        Assert.Contains("offset", message);
    }

    [Fact]
    public void UnknownToolGetsGenericAdvice()
    {
        CallToolResult replaced = ResponseSizeGuard.Limit("vault_new", readOnly: true, Text(30000), 20000);

        string message = Assert.IsType<TextContentBlock>(Assert.Single(replaced.Content)).Text;
        Assert.Contains("Narrow the selection", message);
    }

    [Fact]
    public void OversizedWriteResultIsNotReportedAsFailure()
    {
        CallToolResult replaced = ResponseSizeGuard.Limit("vault_move_items", readOnly: false, Text(30000), 20000);

        Assert.NotEqual(true, replaced.IsError);
        string message = Assert.IsType<TextContentBlock>(Assert.Single(replaced.Content)).Text;
        Assert.Contains("was done", message);
    }

    [Fact]
    public void OversizedDryRunIsAnErrorAndSaysNothingWasWritten()
    {
        CallToolResult replaced = ResponseSizeGuard.Limit(
            "vault_copy_components", readOnly: false, Text(30000), 20000, dryRun: true);

        Assert.True(replaced.IsError);
        string message = Assert.IsType<TextContentBlock>(Assert.Single(replaced.Content)).Text;
        Assert.Contains("The dry run wrote nothing", message);
        Assert.DoesNotContain("was done", message);
    }

    [Fact]
    public void ErrorResultsAreNeverReplaced()
    {
        CallToolResult error = new()
        {
            IsError = true,
            Content = [new TextContentBlock { Text = new string('x', 30000) }],
        };

        Assert.Same(error, ResponseSizeGuard.Limit("vault_table", true, error, 20000));
    }
}
