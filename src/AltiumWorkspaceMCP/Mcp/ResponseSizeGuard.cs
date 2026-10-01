using AltiumWorkspaceMCP.Tools;
using ModelContextProtocol.Protocol;

namespace AltiumWorkspaceMCP.Mcp;

/// <summary>
/// The last defense against a response the client would reject: an over-long result
/// is replaced with a clear refusal and advice on how to narrow the selection.
/// </summary>
/// <remarks>
/// The client accepts a tool result of roughly up to 25 thousand tokens; it drops a longer
/// response entirely, and the agent is left with no data and no explanation (that is how
/// a 64-thousand-character <c>vault_folders</c> response died on 2026-09-17). The normal way is
/// paging inside the tools themselves; this catches what they missed.
/// </remarks>
internal static class ResponseSizeGuard
{
    /// <summary>Advice per tool: how to narrow the selection. Other tools get the generic one.</summary>
    private static readonly Dictionary<string, string> Advice = new(StringComparer.Ordinal)
    {
        ["vault_table"] = "Lower limit (default 100), list only the needed parameters in columns, "
            + "page with offset/nextOffset or narrow the selection: folder, hrid, parameterEquals.",
        ["vault_components"] = "Lower limit (default 50), list only the needed parameters in columns, "
            + "page with offset/nextOffset or narrow the selection: type, filters, text.",
        ["vault_folders"] = "Call with under for the needed branch or nameContains to search, lower depth.",
        ["vault_check_components"] = "Check in smaller sets: components in parts or a narrow folder; "
            + "the detailed list is paged with offset/nextOffset.",
        ["vault_cleanup_parameters"] = "Narrow the selection: components in parts or a narrow folder, hrid.",
        ["vault_audit"] = "Lower count or page with offset/nextOffset.",
        ["vault_component_detail"] = "Request components one at a time; for the parameters of many parts use vault_table.",
        ["vault_revision_parameters"] = "Specify the revision and the list of parameters.",
        ["vault_templates"] = "Request one template: vault_template show.",
        ["vault_component_types"] = "For the type tree action=list is enough; for assignment pass components in parts.",
    };

    private const string GenericAdvice = "Narrow the selection or request a smaller portion.";

    /// <summary>
    /// Returns the result as is if it fits into <paramref name="maxChars"/>, otherwise a
    /// replacement: for reads — a refusal with advice, for writes — a message that the operation
    /// was done but the response is too long (a refusal would mislead the agent).
    /// </summary>
    public static CallToolResult Limit(
        string tool,
        bool readOnly,
        CallToolResult result,
        int maxChars,
        bool dryRun = false)
    {
        if (result.IsError == true || maxChars <= 0)
        {
            return result;
        }

        int length = SizeOf(result);
        if (length <= maxChars)
        {
            return result;
        }

        // Write and dry run: the summary in full, details — as many as fit.
        if ((dryRun || !readOnly) && TryFit(result, maxChars) is { } fitted)
        {
            return fitted;
        }

        string advice = Advice.GetValueOrDefault(tool, GenericAdvice);

        // A dry run executed nothing: for the agent it is a refusal, like a read.
        readOnly |= dryRun;

        string text = readOnly
            ? $"Response of tool {tool} is {length} characters, over the limit {maxChars} "
                + $"(ALTIUM_MAX_RESPONSE_CHARS)." + (dryRun ? " The dry run wrote nothing." : string.Empty)
                + $" Narrow the selection: {(dryRun ? "pass fewer objects per call; " : string.Empty)}{advice}"
            : $"Operation {tool} was done, but its response is {length} characters, over the limit {maxChars}, "
                + "and was not returned. Check the result with a separate request (vault_table, vault_component_detail, "
                + "vault_audit); next time pass fewer objects per call.";

        return new CallToolResult
        {
            IsError = readOnly,
            Content = [new TextContentBlock { Text = text }],
        };
    }

    /// <summary>Shortens a JSON response by the <see cref="ResponseFit"/> rule; <see langword="null"/> — it cannot be shortened.</summary>
    private static CallToolResult? TryFit(CallToolResult result, int maxChars)
    {
        if (result.StructuredContent is not null
            || result.Content.Count != 1
            || result.Content[0] is not TextContentBlock { Text: { } text })
        {
            return null;
        }

        string? fitted = ResponseFit.Fit(text, maxChars);

        return fitted is null
            ? null
            : new CallToolResult { Content = [new TextContentBlock { Text = fitted }] };
    }

    /// <summary>Length of the text content of a result.</summary>
    internal static int SizeOf(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().Sum(block => block.Text?.Length ?? 0);
}
