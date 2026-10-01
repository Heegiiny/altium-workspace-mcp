using System.Text.Json;
using AltiumWorkspaceMCP.Configuration;
using AltiumWorkspaceMCP.Safety;
using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>Confirmation rules for bulk edits: threshold, tokens, preview.</summary>
public sealed class ChangeGuardTests
{
    private static ChangeGuard Guard(
        WriteMode mode = WriteMode.Guarded,
        int threshold = 4,
        params string[] writable) =>
        new(new VaultOptions
        {
            BaseUrl = new Uri("http://localhost:9780"),
            StateDirectory = Path.GetTempPath(),
            ExchangeDirectory = Path.GetTempPath(),
            WriteMode = mode,
            ConfirmThreshold = threshold,
            WritableFolders = writable,
        });

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(500, true)]
    public void GuardedRequiresConfirmationOnlyAboveThreshold(int affected, bool expected) =>
        Assert.Equal(expected, Guard().RequiresConfirmation("op", affected, []));

    [Fact]
    public void UnguardedNeverRequiresConfirmation() =>
        Assert.False(Guard(WriteMode.Unguarded).RequiresConfirmation("op", 1000, []));

    [Fact]
    public void ReadOnlyRejectsWrites()
    {
        var error = Assert.Throws<ChangeRejectedException>(() => Guard(WriteMode.ReadOnly).EnsureAllowed("op", 1, []));
        Assert.Contains("read-only", error.Message);
    }

    [Fact]
    public async Task ReadOnlyAllowsDryRun()
    {
        ChangeGuard guard = Guard(WriteMode.ReadOnly);

        await DryRun.RunAsync("op", dryRun: true, () =>
        {
            guard.EnsureAllowed("op", 50, []);
            return Task.FromResult<object>(new { });
        });
    }

    [Fact]
    public async Task DryRunNeverRequiresConfirmation()
    {
        ChangeGuard guard = Guard();
        bool requires = true;

        await DryRun.RunAsync("op", dryRun: true, () =>
        {
            requires = guard.RequiresConfirmation("op", 100, []);
            return Task.FromResult<object>(new { });
        });

        Assert.False(requires);
    }

    [Fact]
    public void EmptyChangeIsRejected() =>
        Assert.Throws<ChangeRejectedException>(() => Guard().EnsureAllowed("op", 0, []));

    [Fact]
    public void FoldersOutsideWritableAreRejected()
    {
        ChangeGuard guard = Guard(WriteMode.Guarded, 4, @"Components\Test");

        guard.EnsureAllowed("op", 1, [@"Components\Test\Sub"]);

        var error = Assert.Throws<ChangeRejectedException>(
            () => guard.EnsureAllowed("op", 1, [@"Components\Passive"]));
        Assert.Contains(@"Components\Passive", error.Message);
    }

    [Fact]
    public void TryDeferReturnsNullBelowThresholdAndTokenAbove()
    {
        ChangeGuard guard = Guard();

        Assert.Null(guard.TryDefer("op", 4, [], "small edit", "payload"));

        PendingChange? change = guard.TryDefer("op", 5, [], "large edit", "payload");
        Assert.NotNull(change);
        Assert.Equal(5, change.Affected);
        Assert.True(change.ExpiresAt > change.CreatedAt);
        Assert.Single(guard.ListPending());
    }

    [Fact]
    public void TokenIsSingleUseAndBoundToOperation()
    {
        ChangeGuard guard = Guard();
        PendingChange change = guard.Defer("op-a", 9, "s", "p");

        Assert.Throws<ChangeRejectedException>(() => guard.Consume(change.Token, "op-b"));

        // A token used with another operation is consumed too.
        Assert.Throws<ChangeRejectedException>(() => guard.Consume(change.Token, "op-a"));

        PendingChange second = guard.Defer("op-a", 9, "s", "p");
        Assert.Equal("op-a", guard.Consume(second.Token, "op-a").Operation);
        Assert.Throws<ChangeRejectedException>(() => guard.Consume(second.Token, "op-a"));
    }

    [Fact]
    public void ConfirmationLimitsPreviewToTwentyItems()
    {
        ChangeGuard guard = Guard();
        PendingChange change = guard.Defer("op", 30, "s", "p");
        var preview = Enumerable.Range(1, 30).Select(number => (object)$"item {number}").ToList();

        JsonElement json = JsonSerializer.SerializeToElement(guard.Confirmation(change, preview));

        Assert.True(json.GetProperty("confirmationRequired").GetBoolean());
        Assert.Equal(20, json.GetProperty("preview").GetArrayLength());
        Assert.Contains("20 of 30", json.GetProperty("previewNote").GetString());
        Assert.Equal(change.Token, json.GetProperty("confirmToken").GetString());
        Assert.Equal(30, json.GetProperty("affected").GetInt32());
    }

    // ── ConfirmationGate: the full cycle "preview → token → apply" ─────────────────

    /// <summary>Sample tool body: counts applications and writes "to the server" through the run journal.</summary>
    private sealed class FakeTool(ChangeGuard guard, int affected)
    {
        public int Applied;

        public Task<object> RunAsync(string? token) =>
            guard.RunAsync("fake", token, BodyAsync, CancellationToken.None);

        private async Task<object> BodyAsync(ConfirmationGate gate, CancellationToken cancellationToken)
        {
            if (await gate.DeferIfLargeAsync(affected, [], $"edit {affected}", cancellationToken: cancellationToken) is { } confirmation)
            {
                return confirmation;
            }

            var writes = Enumerable.Range(1, affected).Select(number => $"CMP-{number:000}").ToList();

            if (!DryRun.Intercept("fake_write", writes))
            {
                Applied += affected;
            }

            return new { applied = affected };
        }
    }

    [Fact]
    public async Task SmallChangeAppliesImmediately()
    {
        var tool = new FakeTool(Guard(), 4);

        await tool.RunAsync(token: null);

        Assert.Equal(4, tool.Applied);
    }

    [Fact]
    public async Task LargeChangeReturnsPreviewFromDryRunAndWritesNothing()
    {
        ChangeGuard guard = Guard();
        var tool = new FakeTool(guard, 5);

        JsonElement json = JsonSerializer.SerializeToElement(await tool.RunAsync(token: null));

        Assert.Equal(0, tool.Applied);
        Assert.True(json.GetProperty("confirmationRequired").GetBoolean());
        Assert.Equal(5, json.GetProperty("preview").GetArrayLength());
        Assert.Contains("CMP-001", json.GetProperty("preview")[0].GetString());
        Assert.Single(guard.ListPending());
    }

    [Fact]
    public async Task TokenAppliesTheShownChangeExactlyOnce()
    {
        ChangeGuard guard = Guard();
        var tool = new FakeTool(guard, 7);

        JsonElement preview = JsonSerializer.SerializeToElement(await tool.RunAsync(token: null));
        string token = preview.GetProperty("confirmToken").GetString()!;

        await tool.RunAsync(token);

        Assert.Equal(7, tool.Applied);
        Assert.Empty(guard.ListPending());

        await Assert.ThrowsAsync<ChangeRejectedException>(() => tool.RunAsync(token));
        Assert.Equal(7, tool.Applied);
    }

    [Fact]
    public async Task DryRunWithTokenDoesNotConsumeIt()
    {
        ChangeGuard guard = Guard();
        var tool = new FakeTool(guard, 6);

        JsonElement preview = JsonSerializer.SerializeToElement(await tool.RunAsync(token: null));
        string token = preview.GetProperty("confirmToken").GetString()!;

        await DryRun.RunAsync("fake", dryRun: true, () => tool.RunAsync(token));

        Assert.Equal(0, tool.Applied);
        Assert.Single(guard.ListPending());
    }

    [Fact]
    public async Task TokenOfAnotherOperationIsRejected()
    {
        ChangeGuard guard = Guard();
        PendingChange change = guard.Defer("other", 9, "s", "p", _ => Task.FromResult<object>(new { }));
        var tool = new FakeTool(guard, 5);

        await Assert.ThrowsAsync<ChangeRejectedException>(() => tool.RunAsync(change.Token));
    }

    [Fact]
    public async Task ConfirmedChangeThatGrewAfterPreviewIsRejected()
    {
        ChangeGuard guard = Guard();
        int affected = 5;

        Task<object> Run(string? token) => guard.RunAsync("grow", token, async (gate, ct) =>
        {
            if (await gate.DeferIfLargeAsync(affected, [], "s", cancellationToken: ct) is { } confirmation)
            {
                return confirmation;
            }

            DryRun.Intercept("write", new[] { "CMP-001" });
            return new { applied = affected };
        }, CancellationToken.None);

        JsonElement preview = JsonSerializer.SerializeToElement(await Run(null));
        string token = preview.GetProperty("confirmToken").GetString()!;

        affected = 9;

        var error = await Assert.ThrowsAsync<ChangeRejectedException>(() => Run(token));
        Assert.Contains("data changed", error.Message);
    }

    [Fact]
    public async Task LargeChangeThatWritesNothingNeedsNoToken()
    {
        ChangeGuard guard = Guard();

        JsonElement json = JsonSerializer.SerializeToElement(await guard.RunAsync("empty", null, async (gate, ct) =>
        {
            if (await gate.DeferIfLargeAsync(8, [], "empty", cancellationToken: ct) is { } confirmation)
            {
                return confirmation;
            }

            return new { applied = 0 };
        }, CancellationToken.None));

        Assert.False(json.GetProperty("confirmationRequired").GetBoolean());
        Assert.Empty(guard.ListPending());
    }

    [Fact]
    public void PreviewShortensLongRecords()
    {
        string shortened = DryRunPreview.Shorten(new { text = new string('x', 1000) });

        Assert.True(shortened.Length <= DryRunPreview.ItemLength + 2);
        Assert.EndsWith("…", shortened);
    }
}
