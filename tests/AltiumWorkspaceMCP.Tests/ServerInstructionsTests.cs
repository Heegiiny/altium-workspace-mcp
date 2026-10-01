using AltiumWorkspaceMCP.Mcp;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>MCP server instructions: embedded in the assembly and short.</summary>
public sealed class ServerInstructionsTests
{
    [Fact]
    public void TextIsEmbeddedAndWithinLimit()
    {
        string text = ServerInstructions.Text;

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.True(text.Length <= ServerInstructions.MaxLength, $"{text.Length} characters at limit {ServerInstructions.MaxLength}");
    }

    [Theory]
    [InlineData("vault_folders")]
    [InlineData("parameterEquals")]
    [InlineData("dryRun")]
    [InlineData("confirmToken")]
    [InlineData("vault_session")]
    [InlineData("footprints")]
    [InlineData("footprintMode=add")]
    public void TextMentionsTheKeyRules(string fragment) =>
        Assert.Contains(fragment, ServerInstructions.Text);

    [Fact]
    public void ThresholdIsSubstitutedFromOptions()
    {
        Assert.Contains("more than 4 objects", ServerInstructions.For(4));
        Assert.Contains("more than 7 objects", ServerInstructions.For(7));
        Assert.DoesNotContain("{threshold}", ServerInstructions.For(4));
    }

    [Fact]
    public void TextMentionsModelsBeforeFolderDeletion()
    {
        Assert.Contains("before deleting the folder", ServerInstructions.Text);
        Assert.Contains("vault_restore_items", ServerInstructions.Text);
    }
}
