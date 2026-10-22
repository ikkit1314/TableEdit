using TableEdit.Model;

namespace TableEdit;

public enum SearchScope { VisibleCells, CurrentColumn, SelectedCells }

/// <summary>A captured region, independent of the cell cursor used to navigate search results.</summary>
public sealed class SearchSelection
{
    internal HashSet<Row> Rows { get; }
    internal int FirstColumn { get; }
    internal int LastColumn { get; }
    internal long ColumnRevision { get; }
    public long CellCount => (long)Rows.Count * (LastColumn - FirstColumn + 1);

    internal SearchSelection(IEnumerable<Row> rows, int firstColumn, int lastColumn, long columnRevision)
    {
        Rows = rows.ToHashSet();
        FirstColumn = firstColumn;
        LastColumn = lastColumn;
        ColumnRevision = columnRevision;
    }
}
