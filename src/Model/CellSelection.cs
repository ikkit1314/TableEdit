namespace TableEdit.Model;

/// <summary>A rectangular selection in the current visible row order. The anchor stays fixed during extension.</summary>
public sealed class CellSelection
{
    public bool HasSelection { get; private set; }
    public int AnchorRow { get; private set; }
    public int AnchorColumn { get; private set; }
    public int Row { get; private set; }
    public int Column { get; private set; }
    public int Top => Math.Min(AnchorRow, Row);
    public int Bottom => Math.Max(AnchorRow, Row);
    public int Left => Math.Min(AnchorColumn, Column);
    public int Right => Math.Max(AnchorColumn, Column);
    public long Count => HasSelection ? (long)(Bottom - Top + 1) * (Right - Left + 1) : 0;

    public void Select(int row, int column, bool extend = false)
    {
        if (!extend || !HasSelection) (AnchorRow, AnchorColumn) = (row, column);
        (Row, Column) = (row, column);
        HasSelection = true;
    }

    public bool Contains(int row, int column) => HasSelection &&
        row >= Top && row <= Bottom && column >= Left && column <= Right;

    public void Clear() => HasSelection = false;
}
