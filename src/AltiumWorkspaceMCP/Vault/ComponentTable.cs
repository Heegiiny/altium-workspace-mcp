namespace AltiumWorkspaceMCP.Vault;

/// <summary>
/// Components as a table: a list of columns and rows of values.
/// </summary>
/// <remarks>
/// The same data as an array of objects takes three times more: the parameter
/// names are repeated in every record. For a selection of thousands of components this is
/// the difference between hundreds of kilobytes and megabytes, that is, between a response suitable
/// and unsuitable for passing to a model. The table form also matches
/// how components are edited in Altium through Batch Edit.
/// </remarks>
public sealed class ComponentTable
{
    /// <summary>
    /// Attribute columns come first and are not component parameters.
    /// "comment" is the component name that Altium puts into the schematic.
    /// </summary>
    public static readonly string[] FixedColumns = ["hrid", "folder", "revision", "comment", "description"];

    private ComponentTable(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string?>> rows)
    {
        Columns = columns;
        Rows = rows;
    }

    public IReadOnlyList<string> Columns { get; }

    public IReadOnlyList<IReadOnlyList<string?>> Rows { get; }

    /// <summary>
    /// Builds the table from a selection. Parameter columns are the union of the parameters of all
    /// components in the selection, so the rows have equal length and are comparable.
    /// </summary>
    /// <param name="parameterColumns">
    /// If set, only these parameters go into the table. Empty — all that were found.
    /// </param>
    public static ComponentTable Build(
        IReadOnlyList<ComponentRecord> records,
        IReadOnlyList<string>? parameterColumns = null)
    {
        IReadOnlyList<string> parameters = parameterColumns is { Count: > 0 }
            ? parameterColumns
            : records
                .SelectMany(record => record.Parameters.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

        var columns = FixedColumns.Concat(parameters).ToList();

        var rows = records.Select(record =>
        {
            var row = new List<string?>(columns.Count)
            {
                record.Hrid,
                record.FolderPath,
                record.RevisionId,
                record.Comment,
                record.Description,
            };

            foreach (string parameter in parameters)
            {
                row.Add(record.Parameters.TryGetValue(parameter, out string? value) ? value : null);
            }

            return (IReadOnlyList<string?>)row;
        }).ToList();

        return new ComponentTable(columns, rows);
    }

    /// <summary>
    /// Parses the table back into edits by component.
    /// </summary>
    /// <remarks>
    /// The hrid column is required — a row is matched to a component by it.
    /// The description and the component name are written like parameters; the folder and the revision
    /// number are skipped on parsing: the folder is changed by a move, and the revision number
    /// is assigned by the server.
    /// </remarks>
    /// <param name="emptyMeansDelete">
    /// By default an empty parameter cell is an empty value. If true — an empty
    /// cell deletes the parameter from the revision entirely (see <see cref="TableEdit.DeleteParameters"/>).
    /// </param>
    public static IReadOnlyList<TableEdit> Parse(
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<string?>> rows,
        bool emptyMeansDelete = false)
    {
        int hridIndex = IndexOf(columns, "hrid");
        if (hridIndex < 0)
        {
            throw new ArgumentException(
                "The table has no 'hrid' column — rows are matched to components by it.",
                nameof(columns));
        }

        int descriptionIndex = IndexOf(columns, "description");
        int commentIndex = IndexOf(columns, "comment");

        var skipped = new HashSet<string>(FixedColumns, StringComparer.OrdinalIgnoreCase);

        var edits = new List<TableEdit>(rows.Count);

        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            IReadOnlyList<string?> row = rows[rowIndex];

            if (row.Count != columns.Count)
            {
                throw new ArgumentException(
                    $"Row {rowIndex + 1} has {row.Count} values for {columns.Count} columns.",
                    nameof(rows));
            }

            string? component = row[hridIndex];
            if (string.IsNullOrWhiteSpace(component))
            {
                throw new ArgumentException($"In row {rowIndex + 1} the 'hrid' column is empty.", nameof(rows));
            }

            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var deleteParameters = new List<string>();

            for (int column = 0; column < columns.Count; column++)
            {
                if (skipped.Contains(columns[column]))
                {
                    continue;
                }

                if (emptyMeansDelete && string.IsNullOrEmpty(row[column]))
                {
                    deleteParameters.Add(columns[column]);
                    continue;
                }

                // An empty cell means an empty parameter value, not "do not change":
                // otherwise erasing a value through the table would be impossible.
                parameters[columns[column]] = row[column] ?? string.Empty;
            }

            edits.Add(new TableEdit(
                component.Trim(),
                parameters,
                deleteParameters,
                descriptionIndex >= 0 ? row[descriptionIndex] : null,
                commentIndex >= 0 ? row[commentIndex] : null));
        }

        return edits;
    }

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (int index = 0; index < columns.Count; index++)
        {
            if (string.Equals(columns[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }
}

/// <summary>An edit of one component, parsed from a table row.</summary>
public sealed record TableEdit(
    string Component,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyList<string> DeleteParameters,
    string? Description,
    string? Comment);
