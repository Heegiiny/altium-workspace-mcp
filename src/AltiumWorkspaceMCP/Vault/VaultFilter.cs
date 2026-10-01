namespace AltiumWorkspaceMCP.Vault;

/// <summary>
/// Building Vault service filter expressions. The syntax is SQL-like:
/// <c>FolderGUID = 'A1B2...' AND HRID LIKE 'RES-%'</c>.
/// </summary>
/// <remarks>
/// Values must pass through <see cref="Literal"/>: a string parameter
/// coming from the caller could otherwise break out of the quote and change
/// the meaning of the condition.
/// </remarks>
public static class VaultFilter
{
    /// <summary>Wraps a value in quotes, doubling the inner ones.</summary>
    public static string Literal(string? value) =>
        value is null ? "''" : $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    public static string Equal(string field, string? value) =>
        $"{ValidateField(field)} = {Literal(value)}";

    public static string Like(string field, string pattern) =>
        $"{ValidateField(field)} LIKE {Literal(pattern)}";

    /// <summary>
    /// The largest number of values in one IN list.
    /// </summary>
    /// <remarks>
    /// The vault database rejects lists longer than 1500 values with the error
    /// "Too many values (more than 1500) in member list". The margin is intentional:
    /// an expression may contain several IN conditions at once.
    /// </remarks>
    public const int MaxInValues = 1000;

    /// <summary>A set membership condition. An empty set gives a deliberately false condition.</summary>
    public static string In(string field, IReadOnlyCollection<string> values)
    {
        if (values.Count == 0)
        {
            return "1 = 0";
        }

        if (values.Count > MaxInValues)
        {
            throw new ArgumentException(
                $"{values.Count} values were passed to the IN list with the limit {MaxInValues}. "
                + "Split the selection into parts with Chunk.",
                nameof(values));
        }

        return $"{ValidateField(field)} IN ({string.Join(", ", values.Select(Literal))})";
    }

    /// <summary>
    /// Splits a set of values into parts, each of which fits the limit
    /// of one IN list.
    /// </summary>
    public static IEnumerable<IReadOnlyList<string>> Chunk(IEnumerable<string> values) =>
        values.Chunk(MaxInValues).Select(chunk => (IReadOnlyList<string>)chunk);

    public static string And(params string?[] conditions) => Combine("AND", conditions);

    public static string Or(params string?[] conditions) => Combine("OR", conditions);

    private static string Combine(string keyword, IEnumerable<string?> conditions)
    {
        var parts = conditions.Where(part => !string.IsNullOrWhiteSpace(part)).ToList();
        return parts.Count switch
        {
            0 => string.Empty,
            1 => parts[0]!,
            _ => string.Join($" {keyword} ", parts.Select(part => $"({part})")),
        };
    }

    /// <summary>
    /// The field name is substituted into the expression as is, so only
    /// simple identifiers are allowed.
    /// </summary>
    private static string ValidateField(string field)
    {
        if (string.IsNullOrWhiteSpace(field) || !field.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            throw new ArgumentException(
                $"Invalid filter field name: '{field}'. Letters, digits and underscore are allowed.",
                nameof(field));
        }

        return field;
    }
}
