using AltiumWorkspaceMCP.Configuration;
using AltiumWorkspaceMCP.Safety;
using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Tests;

/// <summary>Approval of a plan for the whole work: hit, charge, period, second plan.</summary>
public sealed class WorkPlanTests
{
    private static ChangeGuard Guard(int threshold = 4) => new(new VaultOptions
    {
        BaseUrl = new Uri("http://localhost:9780"),
        StateDirectory = Path.GetTempPath(),
        ExchangeDirectory = Path.GetTempPath(),
        WriteMode = WriteMode.Guarded,
        ConfirmThreshold = threshold,
    });

    private static WorkPlan CreateApproved(
        ChangeGuard guard,
        string[]? operations = null,
        string[]? folders = null,
        int maxObjects = 20,
        double hours = 0)
    {
        WorkPlan plan = guard.CreatePlan(
            "resistor migration",
            operations ?? ["vault_update_parameters"],
            folders ?? [@"Components\TestRoot-20260910"],
            maxObjects,
            hours);

        return guard.ApprovePlan(plan.Token);
    }

    // ── Plan creation ───────────────────────────────────────────────────────

    [Fact]
    public void CreateRejectsMissingSummaryOperationsFoldersOrObjects()
    {
        ChangeGuard guard = Guard();

        Assert.Throws<ChangeRejectedException>(() => guard.CreatePlan("", ["op"], ["folder"], 10, 0));
        Assert.Throws<ChangeRejectedException>(() => guard.CreatePlan("s", [], ["folder"], 10, 0));
        Assert.Throws<ChangeRejectedException>(() => guard.CreatePlan("s", ["op"], [], 10, 0));
        Assert.Throws<ChangeRejectedException>(() => guard.CreatePlan("s", ["op"], ["folder"], 0, 0));
    }

    [Fact]
    public void CreateRejectsHoursAboveEight() =>
        Assert.Throws<ChangeRejectedException>(() => Guard().CreatePlan("s", ["op"], ["folder"], 10, 9));

    [Fact]
    public void CreateDefaultsToTwoHours()
    {
        WorkPlan plan = Guard().CreatePlan("s", ["op"], ["folder"], 10, 0);

        Assert.True(plan.ExpiresAt <= plan.CreatedAt + TimeSpan.FromHours(2).Add(TimeSpan.FromSeconds(1)));
        Assert.True(plan.ExpiresAt > plan.CreatedAt + TimeSpan.FromMinutes(119));
    }

    [Fact]
    public void SecondCreateIsRejectedWhileFirstIsActive()
    {
        ChangeGuard guard = Guard();
        guard.CreatePlan("first", ["op"], ["folder"], 10, 0);

        var error = Assert.Throws<ChangeRejectedException>(() => guard.CreatePlan("second", ["op"], ["folder"], 10, 0));
        Assert.Contains("already active", error.Message);
    }

    [Fact]
    public void CreateAfterCloseSucceeds()
    {
        ChangeGuard guard = Guard();
        guard.CreatePlan("first", ["op"], ["folder"], 10, 0);
        guard.ClosePlan();

        WorkPlan second = guard.CreatePlan("second", ["op"], ["folder"], 10, 0);
        Assert.Equal(2, second.Number);
    }

    [Fact]
    public async Task CreateAfterExpirySucceedsWithoutClose()
    {
        ChangeGuard guard = Guard();
        // A one-second plan: the real wait is shorter than the usual test timeout.
        guard.CreatePlan("first", ["op"], ["folder"], 10, 1.0 / 3600);

        await Task.Delay(TimeSpan.FromSeconds(1.2));

        // An "expired" plan is enough to create a new one: closing explicitly is not required.
        WorkPlan second = guard.CreatePlan("second", ["op"], ["folder"], 10, 0);
        Assert.Equal(2, second.Number);
    }

    // ── Approval ─────────────────────────────────────────────────────────────

    [Fact]
    public void ApproveRejectsUnknownToken() =>
        Assert.Throws<ChangeRejectedException>(() => Guard().ApprovePlan("nonexistent"));

    [Fact]
    public void ApproveResetsExpiryFromApprovalMoment()
    {
        ChangeGuard guard = Guard();
        WorkPlan plan = guard.CreatePlan("s", ["op"], ["folder"], 10, 1);
        DateTimeOffset createdExpiry = plan.ExpiresAt;

        WorkPlan approved = guard.ApprovePlan(plan.Token);

        Assert.True(approved.Approved);
        Assert.True(approved.ExpiresAt >= createdExpiry);
    }

    // ── Coverage: operation, folders, volume, period ────────────────────────────

    [Fact]
    public void UnapprovedPlanDoesNotCoverAnything()
    {
        ChangeGuard guard = Guard();
        WorkPlan plan = guard.CreatePlan("s", ["vault_update_parameters"], [@"Components\Test"], 10, 0);

        Assert.False(plan.CanCover("vault_update_parameters", 5, [@"Components\Test"], DateTimeOffset.Now));
    }

    [Fact]
    public void OperationOutsideListDoesNotCover()
    {
        WorkPlan plan = CreateApproved(Guard(), operations: ["vault_update_parameters"]);

        Assert.False(plan.CanCover("vault_move_items", 5, [@"Components\TestRoot-20260910"], DateTimeOffset.Now));
    }

    [Fact]
    public void OperationWithSuffixIsCoveredByBaseName()
    {
        WorkPlan plan = CreateApproved(Guard(), operations: ["vault_folder"]);

        Assert.True(plan.CanCover("vault_folder:create", 1, [@"Components\TestRoot-20260910"], DateTimeOffset.Now));
    }

    [Fact]
    public void FolderOutsideRootsDoesNotCover()
    {
        WorkPlan plan = CreateApproved(Guard(), folders: [@"Components\TestRoot-20260910"]);

        Assert.False(plan.CanCover(
            "vault_update_parameters", 5, [@"Components\Passive Components\Resistors"], DateTimeOffset.Now));
    }

    [Fact]
    public void SubfolderOfRootIsCovered()
    {
        WorkPlan plan = CreateApproved(Guard(), folders: [@"Components\TestRoot-20260910"]);

        Assert.True(plan.CanCover(
            "vault_update_parameters", 5, [@"Components\TestRoot-20260910\Sub"], DateTimeOffset.Now));
    }

    [Fact]
    public void SimilarlyNamedSiblingFolderIsNotCovered()
    {
        // "…Test-2026091" must not match the root "…Test-20260910" by string prefix.
        WorkPlan plan = CreateApproved(Guard(), folders: [@"Components\TestRoot-20260910"]);

        Assert.False(plan.CanCover(
            "vault_update_parameters", 1, [@"Components\TestRoot-202609100ther"], DateTimeOffset.Now));
    }

    [Fact]
    public void AffectedAboveRemainingDoesNotCover()
    {
        WorkPlan plan = CreateApproved(Guard(), maxObjects: 10);

        Assert.False(plan.CanCover("vault_update_parameters", 11, [@"Components\TestRoot-20260910"], DateTimeOffset.Now));
        Assert.True(plan.CanCover("vault_update_parameters", 10, [@"Components\TestRoot-20260910"], DateTimeOffset.Now));
    }

    [Fact]
    public void ExpiredPlanDoesNotCover()
    {
        WorkPlan plan = CreateApproved(Guard());

        Assert.False(plan.CanCover(
            "vault_update_parameters", 1, [@"Components\TestRoot-20260910"], plan.ExpiresAt + TimeSpan.FromSeconds(1)));
    }

    // ── RequiresConfirmation with a plan ────────────────────────────────────────

    [Fact]
    public void RequiresConfirmationFalseUnderApprovedPlan()
    {
        ChangeGuard guard = Guard();
        CreateApproved(guard, maxObjects: 20);

        bool requires = guard.RequiresConfirmation(
            "vault_update_parameters", 10, [@"Components\TestRoot-20260910"]);

        Assert.False(requires);
    }

    [Fact]
    public void RequiresConfirmationTrueOutsidePlanRoots()
    {
        ChangeGuard guard = Guard();
        CreateApproved(guard, folders: [@"Components\TestRoot-20260910"]);

        bool requires = guard.RequiresConfirmation(
            "vault_update_parameters", 10, [@"Components\Passive Components\Resistors"]);

        Assert.True(requires);
    }

    [Fact]
    public void CheckingRequiresConfirmationDoesNotChargeThePlan()
    {
        ChangeGuard guard = Guard();
        WorkPlan plan = CreateApproved(guard, maxObjects: 20);

        guard.RequiresConfirmation("vault_update_parameters", 10, [@"Components\TestRoot-20260910"]);
        guard.RequiresConfirmation("vault_update_parameters", 10, [@"Components\TestRoot-20260910"]);

        Assert.Equal(20, plan.Remaining);
    }

    // ── Charge — only after a successful write, by applied ───────────────────

    [Fact]
    public void ChargeIfPlannedReducesRemainingByAppliedNotAffected()
    {
        ChangeGuard guard = Guard();
        WorkPlan plan = CreateApproved(guard, maxObjects: 20);

        PlanChargeReceipt? receipt = guard.ChargeIfPlanned(
            "vault_update_parameters", 7, [@"Components\TestRoot-20260910"]);

        Assert.NotNull(receipt);
        Assert.Equal(13, receipt.Remaining);
        Assert.Equal(13, plan.Remaining);
        Assert.Equal(7, plan.Applied);
    }

    [Fact]
    public void ChargeIfPlannedReturnsNullWhenOperationOrFolderDoesNotMatch()
    {
        ChangeGuard guard = Guard();
        WorkPlan plan = CreateApproved(guard, operations: ["vault_update_parameters"], folders: [@"Components\TestRoot-20260910"]);

        Assert.Null(guard.ChargeIfPlanned("vault_move_items", 5, [@"Components\TestRoot-20260910"]));
        Assert.Null(guard.ChargeIfPlanned("vault_update_parameters", 5, [@"Components\Other"]));
        Assert.Equal(20, plan.Remaining);
    }

    [Fact]
    public void ChargeIfPlannedReturnsNullWithoutApproval()
    {
        ChangeGuard guard = Guard();
        WorkPlan plan = guard.CreatePlan("s", ["vault_update_parameters"], [@"Components\Test"], 20, 0);

        Assert.Null(guard.ChargeIfPlanned("vault_update_parameters", 5, [@"Components\Test"]));
        Assert.Equal(20, plan.Remaining);
    }

    [Fact]
    public void ChargeIfPlannedReturnsNullAfterClose()
    {
        ChangeGuard guard = Guard();
        CreateApproved(guard);
        guard.ClosePlan();

        Assert.Null(guard.ChargeIfPlanned("vault_update_parameters", 5, [@"Components\TestRoot-20260910"]));
    }

    [Fact]
    public async Task DryRunDoesNotChargeThePlan()
    {
        ChangeGuard guard = Guard();
        WorkPlan plan = CreateApproved(guard, maxObjects: 20);

        await DryRun.RunAsync("op", dryRun: true, () =>
        {
            PlanChargeReceipt? receipt = guard.ChargeIfPlanned(
                "vault_update_parameters", 10, [@"Components\TestRoot-20260910"]);

            Assert.Null(receipt);
            return Task.FromResult<object>(new { });
        });

        Assert.Equal(20, plan.Remaining);
    }

    [Fact]
    public void ChargeIfPlannedIgnoresNonPositiveApplied()
    {
        ChangeGuard guard = Guard();
        WorkPlan plan = CreateApproved(guard, maxObjects: 20);

        Assert.Null(guard.ChargeIfPlanned("vault_update_parameters", 0, [@"Components\TestRoot-20260910"]));
        Assert.Equal(20, plan.Remaining);
    }

    // ── Deletion — only if explicitly listed in operations ────────────────────

    [Fact]
    public void DeleteOperationNotCoveredUnlessListedExplicitly()
    {
        WorkPlan plan = CreateApproved(Guard(), operations: ["vault_update_parameters"]);

        Assert.False(plan.CanCover("vault_delete_items", 1, [@"Components\TestRoot-20260910"], DateTimeOffset.Now));
    }

    [Fact]
    public void DeleteOperationCoveredWhenListedExplicitly()
    {
        WorkPlan plan = CreateApproved(Guard(), operations: ["vault_update_parameters", "vault_delete_items"]);

        Assert.True(plan.CanCover("vault_delete_items", 1, [@"Components\TestRoot-20260910"], DateTimeOffset.Now));
    }

    // ── Show and close ──────────────────────────────────────────────────────

    [Fact]
    public void CloseReturnsClosedPlanAndClearsIt()
    {
        ChangeGuard guard = Guard();
        guard.CreatePlan("s", ["op"], ["folder"], 10, 0);

        WorkPlan? closed = guard.ClosePlan();

        Assert.NotNull(closed);
        Assert.Null(guard.CurrentPlan);
    }

    [Fact]
    public void CloseOnNoPlanReturnsNull() => Assert.Null(Guard().ClosePlan());
}
