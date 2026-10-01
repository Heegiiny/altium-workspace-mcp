namespace AltiumWorkspaceMCP.Safety;

/// <summary>
/// What to return in the tool response when an edit is charged to a plan: the plan number,
/// how much is left and until when the plan is valid.
/// </summary>
public sealed record PlanChargeReceipt(int Number, int Remaining, int MaxObjects, DateTimeOffset ExpiresAt)
{
    /// <summary>The line "plan: N of M left, until HH:MM" for the tool response.</summary>
    public string Describe() =>
        $"plan #{Number}: {Remaining} of {MaxObjects} left, until {ExpiresAt:HH:mm}";
}

/// <summary>
/// Approval of the work as a whole: instead of confirming
/// each portion, the agent describes the plan once — which operations, in which folders,
/// how many objects at most and for how long — and after the owner's "yes" writes
/// in large portions without new questions until the volume and period run out.
/// </summary>
/// <remarks>
/// One plan per server process. The volume is charged not for the check (<see cref="CanCover"/>),
/// but after a successful write — by the number of really changed objects (<see cref="Charge"/>);
/// a dry run does not reach the plan at all, since it never gets to the place where
/// the charge happens.
/// </remarks>
public sealed class WorkPlan
{
    private readonly Lock _gate = new();
    private int _applied;

    public WorkPlan(
        string token,
        int number,
        string summary,
        IReadOnlyList<string> operations,
        IReadOnlyList<string> folders,
        int maxObjects,
        DateTimeOffset createdAt,
        TimeSpan lifetime)
    {
        Token = token;
        Number = number;
        Summary = summary;
        Operations = operations;
        Folders = folders;
        MaxObjects = maxObjects;
        CreatedAt = createdAt;
        Lifetime = lifetime;
        ExpiresAt = createdAt + lifetime;
    }

    public string Token { get; }

    public int Number { get; }

    public string Summary { get; }

    public IReadOnlyList<string> Operations { get; }

    public IReadOnlyList<string> Folders { get; }

    public int MaxObjects { get; }

    public DateTimeOffset CreatedAt { get; }

    private TimeSpan Lifetime { get; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public bool Approved { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    public int Applied
    {
        get { lock (_gate) return _applied; }
    }

    public int Remaining
    {
        get { lock (_gate) return Math.Max(0, MaxObjects - _applied); }
    }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    /// <summary>Marks the plan as approved by the owner; the period starts anew from this moment.</summary>
    public void Approve(DateTimeOffset now)
    {
        Approved = true;
        ApprovedAt = now;
        ExpiresAt = now + Lifetime;
    }

    /// <summary>
    /// The operation is covered by the plan: the name matches fully (<c>vault_update_parameters</c>) or by the
    /// part before the colon (<c>vault_folder:create</c> is covered by the entry <c>vault_folder</c>).
    /// </summary>
    public bool SupportsOperation(string operation) =>
        Operations.Contains(operation, StringComparer.Ordinal)
        || Operations.Contains(operation.Split(':')[0], StringComparer.Ordinal);

    /// <summary>All object folders are inside the plan roots (the folder path or its descendant).</summary>
    public bool FoldersWithinRoots(IReadOnlyCollection<string> folderPaths) =>
        folderPaths.All(path => Folders.Any(root => IsWithin(path, root)));

    private static bool IsWithin(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The edit fits the plan: approved, not expired, the operation and folders match,
    /// and the remaining volume is not less than <paramref name="affected"/>.
    /// </summary>
    public bool CanCover(string operation, int affected, IReadOnlyCollection<string> folderPaths, DateTimeOffset now) =>
        Approved
        && !IsExpired(now)
        && SupportsOperation(operation)
        && FoldersWithinRoots(folderPaths)
        && affected <= Remaining;

    /// <summary>Charges the really changed objects. Called once per successful write.</summary>
    public void Charge(int applied)
    {
        if (applied <= 0)
        {
            return;
        }

        lock (_gate)
        {
            _applied += applied;
        }
    }
}
