namespace AltiumWorkspaceMCP.Vault;

/// <summary>A selection condition by a component parameter value.</summary>
/// <param name="Name">Parameter name, for example "Case/Package".</param>
/// <param name="Value">The value; the % sign allows a partial match.</param>
public sealed record ParameterCriterion(string Name, string Value)
{
    public bool IsPattern => Value.Contains('%', StringComparison.Ordinal);

    /// <summary>Parses a condition like "Name=Value".</summary>
    public static ParameterCriterion Parse(string condition)
    {
        int separator = condition.IndexOf('=', StringComparison.Ordinal);
        if (separator <= 0)
        {
            throw new ArgumentException(
                $"A parameter selection condition must look like 'Name=Value', got '{condition}'.",
                nameof(condition));
        }

        return new ParameterCriterion(
            condition[..separator].Trim(),
            condition[(separator + 1)..].Trim());
    }
}

/// <summary>What to search for in the vault.</summary>
public sealed record ComponentCriteria
{
    /// <summary>Path, path ending, name or GUID of the folder; empty — the whole vault.</summary>
    public string? Folder { get; init; }

    /// <summary>
    /// How loosely to match <see cref="Folder"/> against the tree. Reading allows
    /// <see cref="FolderMatchMode.Lenient"/>; a selection for writing stays strict.
    /// </summary>
    public FolderMatchMode FolderMatch { get; init; } = FolderMatchMode.Strict;

    public bool Recursive { get; init; }

    /// <summary>Item identifier; the % sign allows a partial match.</summary>
    public string? Hrid { get; init; }

    /// <summary>Description substring.</summary>
    public string? DescriptionContains { get; init; }

    public IReadOnlyList<ParameterCriterion> Parameters { get; init; } = [];

    /// <summary>
    /// Names of parameters that the component must not have at all — not even a row in the
    /// revision parameters, even with an empty value. Requires <see cref="Folder"/> to be set:
    /// "absence" cannot be expressed by a server filter, so the candidates are read
    /// in full and filtered on the service side.
    /// </summary>
    public IReadOnlyList<string> ParameterMissing { get; init; } = [];

    /// <summary>Content type name, for example altium-component or altium-symbol.</summary>
    public string? ContentType { get; init; }

    public int Limit { get; init; } = 200;

    /// <summary>
    /// How many first found components to skip (paged output).
    /// The order is set by the server; in practice it is stable, so <c>offset</c> pages the selection
    /// without gaps or repeats. The server reads <c>Offset + Limit</c> records and drops the beginning.
    /// </summary>
    public int Offset { get; init; }

    public bool HasAnyFilter =>
        !string.IsNullOrWhiteSpace(Folder)
        || !string.IsNullOrWhiteSpace(Hrid)
        || !string.IsNullOrWhiteSpace(DescriptionContains)
        || Parameters.Count > 0
        || ParameterMissing.Count > 0
        || !string.IsNullOrWhiteSpace(ContentType);
}
