using TableEdit.Model;

namespace TableEdit;

public partial class MainWindow
{
    private int _duplicateColumn = -1;
    private HashSet<string>? _duplicateValues;

    private bool DuplicateFilterActive => _duplicateColumn >= 0 && _duplicateValues != null;

    private bool IsDuplicateCell(Row row, int column) => DuplicateFilterActive && column == _duplicateColumn &&
        column < row.Cells.Count && _duplicateValues!.Contains(row.Cells[column]);

    private void ResetDuplicateFilter()
    {
        _duplicateColumn = -1;
        _duplicateValues = null;
        _highlight.Refresh();
    }

    /// <summary>
    /// Finds repeated values in one column across the whole document. The first occurrence is
    /// included in the results. Empty cells are ignored; case and whitespace remain significant.
    /// </summary>
    private void FindColumnDuplicates()
    {
        if (!Table.CommitEdit()) return;
        int column = CurrentCol;
        if (column < 0 || column >= _headers.Count)
        {
            StatusText.Text = "Select a cell in the column whose duplicates you want to find.";
            return;
        }

        var values = RepeatedColumnValues(_rows, column, out int matchingRows);
        string name = ColumnName(column);

        // A new duplicate search replaces the previous search, including any text filter.
        // ClearFilter also resets duplicate state, so install the new filter afterwards.
        ClearFilter();
        if (values.Count == 0)
        {
            StatusText.Text = $"No duplicate values in {name} · exact match · empty cells ignored.";
            return;
        }

        _duplicateColumn = column;
        _duplicateValues = values;
        ApplyFilter();
        Table.CurrentColumn = Table.Columns[column];
        StatusText.Text = $"{name}: {values.Count:N0} repeated value(s), {matchingRows:N0} rows " +
            $"({matchingRows - values.Count:N0} extra occurrences) · duplicates highlighted in yellow · exact match · empty cells ignored · Show All to return.";
    }

    private List<Row> FilterDuplicateRows(List<Row> rows)
    {
        if (!DuplicateFilterActive) return rows;
        if (_duplicateColumn >= _headers.Count)
        {
            ResetDuplicateFilter();
            return rows;
        }

        // Recalculate against all rows so deleting/editing an occurrence does not leave a
        // unique value marked as a duplicate. Text filters only narrow the displayed result.
        _duplicateValues = RepeatedColumnValues(_rows, _duplicateColumn, out _);
        return rows.Where(row => _duplicateColumn < row.Cells.Count &&
            _duplicateValues.Contains(row.Cells[_duplicateColumn])).ToList();
    }

    private static HashSet<string> RepeatedColumnValues(IEnumerable<Row> rows, int column, out int matchingRows)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (column < 0 || column >= row.Cells.Count) continue;
            string value = row.Cells[column];
            if (string.IsNullOrEmpty(value)) continue;
            counts.TryGetValue(value, out int count);
            counts[value] = count + 1;
        }

        var repeated = new HashSet<string>(StringComparer.Ordinal);
        matchingRows = 0;
        foreach (var (value, count) in counts)
        {
            if (count < 2) continue;
            repeated.Add(value);
            matchingRows += count;
        }
        return repeated;
    }
}
