using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TableEdit.Model;

namespace TableEdit;

public partial class MainWindow
{
    private readonly CellSelection _cells = new();
    private readonly Dictionary<Row, int> _visibleIndices = new();
    private IPointer? _dragPointer;
    private bool _dragRows;
    private int _columnCursorRow = -1;
    private Point _dragPosition;
    private readonly DispatcherTimer _dragTimer = new() { Interval = TimeSpan.FromMilliseconds(70) };

    private void WireSelection()
    {
        _highlight.IsSelected = (row, col) => _visibleIndices.TryGetValue(row, out int index) && _cells.Contains(index, col);
        // Native DataGrid selection tracks only the editing cursor. Our cell range owns selection visuals.
        foreach (var key in new[] { "DataGridRowSelectedBackgroundBrush", "DataGridRowSelectedHoveredBackgroundBrush",
                     "DataGridRowSelectedUnfocusedBackgroundBrush", "DataGridRowSelectedHoveredUnfocusedBackgroundBrush" })
            Table.Resources[key] = Brushes.Transparent;
        Table.CellPointerPressed += (_, e) =>
        {
            var pointer = e.PointerPressedEventArgs;
            if (e.Row.DataContext is not Row row || !_visibleIndices.TryGetValue(row, out int index)) return;
            int column = Table.Columns.IndexOf(e.Column);
            var point = pointer.GetCurrentPoint(Table);
            if (_editRow == row && _editCol == column && pointer.Source is Visual visual &&
                (visual is TextBox || visual.GetVisualAncestors().Any(v => v is TextBox))) return;
            if (point.Properties.IsRightButtonPressed)
            {
                pointer.Handled = true;
                if (!_cells.Contains(index, column)) SelectCell(index, column);
                return;
            }
            if (!point.Properties.IsLeftButtonPressed) return;
            pointer.Handled = true;
            if (!Table.CommitEdit()) return;
            SelectCell(index, column, pointer.KeyModifiers.HasFlag(KeyModifiers.Shift));
            if (pointer.ClickCount == 2 && !pointer.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                Table.BeginEdit();
                return;
            }
            BeginSelectionDrag(pointer, false);
        };
        Table.AddHandler(PointerPressedEvent, OnRowHeaderPressed, RoutingStrategies.Tunnel);
        Table.AddHandler(PointerMovedEvent, OnSelectionPointerMoved, RoutingStrategies.Tunnel);
        Table.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (_dragPointer == null) return;
            EndSelectionDrag();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        Table.PointerCaptureLost += (_, _) => EndSelectionDrag();
        _dragTimer.Tick += (_, _) => AutoScrollSelection();
        Closed += (_, _) => EndSelectionDrag();
        AddHandler(KeyDownEvent, OnSelectionKeyDown, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnSelectionTextInput, RoutingStrategies.Tunnel);
        SelectRowButton.Click += (_, _) => SelectCurrentRow();
        SelectColumnButton.Click += (_, _) => SelectColumn(CurrentCol);
        DeleteRowsButton.Click += (_, _) => DeleteRows();
        FindDuplicatesButton.Click += (_, _) => FindColumnDuplicates();
        ShowAllButton.Click += (_, _) => ClearFilter();

        MenuItem Action(string title, Action run)
        {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) => run();
            return item;
        }
        Table.ContextMenu = new ContextMenu
        {
            ItemsSource = new Control[]
            {
                Action("Copy Cells", () => _ = CopyCellsAsync()),
                Action("Paste Cells", () => _ = PasteCellsAsync()),
                Action("Clear Cells", ClearSelectedCells),
                Action("Trim Selected Cells", () => TransformSelectedCells("Trim Selected Cells", s => s.Trim())),
                Action("UPPERCASE Selected Cells", () => TransformSelectedCells("Uppercase Selected Cells", s => s.ToUpper(System.Globalization.CultureInfo.CurrentCulture))),
                Action("lowercase Selected Cells", () => TransformSelectedCells("Lowercase Selected Cells", s => s.ToLower(System.Globalization.CultureInfo.CurrentCulture))),
                new Separator(),
                Action("Select Row", SelectCurrentRow),
                Action("Select Column", () => SelectColumn(CurrentCol)),
                Action("Delete Rows", DeleteRows),
                new Separator(),
                Action("Find Duplicates in Column", FindColumnDuplicates),
            }
        };
    }

    private void ReindexVisibleRows()
    {
        _visibleIndices.Clear();
        for (int i = 0; i < _visible.Count; i++) _visibleIndices[_visible[i]] = i;
    }

    private void ClearCellSelection()
    {
        EndSelectionDrag();
        _cells.Clear();
        _columnCursorRow = -1;
        _highlight.Refresh();
    }

    private void SelectCell(int row, int column, bool extend = false)
    {
        if (_visible.Count == 0 || _headers.Count == 0) return;
        row = Math.Clamp(row, 0, _visible.Count - 1);
        column = Math.Clamp(column, 0, _headers.Count - 1);
        _columnCursorRow = -1;
        _cells.Select(row, column, extend);
        Table.SelectedItem = _visible[row];
        Table.CurrentColumn = Table.Columns[column];
        _highlight.Refresh();
        Table.Focus();
        UpdateCellStatus();
    }

    private int CursorRow => _columnCursorRow >= 0 ? _columnCursorRow : _cells.HasSelection ? _cells.Row :
        Table.SelectedItem is Row row && _visibleIndices.TryGetValue(row, out int index) ? index : 0;

    private void SelectCurrentRow()
    {
        if (!Table.CommitEdit() || _visible.Count == 0 || _headers.Count == 0) return;
        int top = _columnCursorRow >= 0 ? _columnCursorRow : _cells.HasSelection ? _cells.Top : CursorRow;
        int bottom = _columnCursorRow >= 0 ? top : _cells.HasSelection ? _cells.Bottom : top;
        SelectCell(top, 0);
        SelectCell(bottom, _headers.Count - 1, true);
    }

    private void SelectColumn(int column, bool fromCurrent = false, bool upwards = false)
    {
        if (column < 0 || column >= _headers.Count || !Table.CommitEdit() || _visible.Count == 0) return;
        int cursor = CursorRow;
        int anchor = fromCurrent ? (_cells.HasSelection ? _cells.AnchorRow : CursorRow) : 0;
        SelectCell(anchor, column);
        SelectCell(upwards ? 0 : _visible.Count - 1, column, true);
        if (fromCurrent) ScrollToCell();
        else
        {
            _columnCursorRow = cursor;
            Table.SelectedItem = _visible[cursor];
        }
    }

    private void SelectAllCells()
    {
        if (!Table.CommitEdit() || _visible.Count == 0 || _headers.Count == 0) return;
        SelectCell(0, 0);
        SelectCell(_visible.Count - 1, _headers.Count - 1, true);
    }

    private void OnRowHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source) return;
        var header = source as DataGridRowHeader ?? source.GetVisualAncestors().OfType<DataGridRowHeader>().FirstOrDefault();
        if (header?.DataContext is not Row row || !_visibleIndices.TryGetValue(row, out int index)) return;
        var properties = e.GetCurrentPoint(Table).Properties;
        if (!properties.IsLeftButtonPressed && !properties.IsRightButtonPressed) return;
        e.Handled = true;
        if (!Table.CommitEdit()) return;
        if (properties.IsRightButtonPressed && _cells.HasSelection && index >= _cells.Top && index <= _cells.Bottom) return;
        int anchor = e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _cells.HasSelection ? _cells.AnchorRow : index;
        SelectCell(anchor, 0);
        SelectCell(index, _headers.Count - 1, true);
        if (properties.IsLeftButtonPressed) BeginSelectionDrag(e, true);
    }

    private void BeginSelectionDrag(PointerPressedEventArgs e, bool rows)
    {
        _dragRows = rows;
        _dragPosition = e.GetPosition(Table);
        _dragPointer = e.Pointer;
        e.Pointer.Capture(Table);
        _dragTimer.Start();
    }

    private void EndSelectionDrag()
    {
        _dragTimer.Stop();
        var pointer = _dragPointer;
        _dragPointer = null;
        if (pointer?.Captured == Table) pointer.Capture(null);
    }

    private void OnSelectionPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragPointer == null) return;
        if (!e.GetCurrentPoint(Table).Properties.IsLeftButtonPressed) { EndSelectionDrag(); return; }
        _dragPosition = e.GetPosition(Table);
        ExtendDragAt(_dragPosition);
        e.Handled = true;
    }

    private void ExtendDragAt(Point point)
    {
        if (Table.InputHitTest(point) is not Visual hit) return;
        var cell = hit as DataGridCell ?? hit.GetVisualAncestors().OfType<DataGridCell>().FirstOrDefault();
        var rowContainer = hit as DataGridRow ?? hit.GetVisualAncestors().OfType<DataGridRow>().FirstOrDefault();
        if (rowContainer?.DataContext is not Row row || !_visibleIndices.TryGetValue(row, out int index)) return;
        if (_dragRows) SelectCell(index, _headers.Count - 1, true);
        else if (cell?.Content is Control { Tag: int col }) SelectCell(index, col, true);
    }

    private void AutoScrollSelection()
    {
        if (_dragPointer == null || !_cells.HasSelection) return;
        var presenter = Table.GetVisualDescendants().OfType<DataGridRowsPresenter>().FirstOrDefault();
        if (presenter?.TranslatePoint(default, Table) is not Point origin) return;
        var bounds = new Rect(origin, presenter.Bounds.Size);
        int row = _cells.Row, col = _cells.Column;
        if (_dragPosition.Y < bounds.Top || _dragPosition.Y > bounds.Bottom)
        {
            var visibleRows = presenter.GetVisualDescendants().OfType<DataGridRow>()
                .Where(r => r.IsVisible && r.DataContext is Row && r.TranslatePoint(default, Table) is Point p &&
                            p.Y + r.Bounds.Height > bounds.Top && p.Y < bounds.Bottom)
                .Select(r => _visibleIndices.GetValueOrDefault((Row)r.DataContext!, row)).ToArray();
            if (_dragPosition.Y < bounds.Top) row = Math.Min(row, visibleRows.DefaultIfEmpty(row).Min()) - 1;
            else row = Math.Max(row, visibleRows.DefaultIfEmpty(row).Max()) + 1;
        }
        if (!_dragRows)
        {
            if (_dragPosition.X < bounds.Left) col--;
            else if (_dragPosition.X > bounds.Right) col++;
        }
        row = Math.Clamp(row, 0, _visible.Count - 1);
        col = Math.Clamp(col, 0, _headers.Count - 1);
        if (row == _cells.Row && col == _cells.Column) return;
        SelectCell(row, col, true);
        ScrollToCell();
    }

    private void ScrollToCell()
    {
        if (_cells.HasSelection) Table.ScrollIntoView(_visible[_cells.Row], Table.Columns[_cells.Column]);
    }

    private async void OnSelectionKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is not Control source || (source != Table && !source.GetVisualAncestors().Contains(Table))) return;
        if (_editRow != null) return;
        bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(Cmd);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        int row = CursorRow, col = Math.Max(0, CurrentCol);
        if (control && (e.Key is Key.C or Key.X))
        {
            e.Handled = true;
            if (await CopyCellsAsync() && e.Key == Key.X) ClearSelectedCells();
        }
        else if (control && e.Key == Key.V && !shift) { e.Handled = true; await PasteCellsAsync(); }
        else if (control && e.Key == Key.A) { e.Handled = true; SelectAllCells(); }
        else if (e.Key == Key.Space && control) { e.Handled = true; SelectColumn(col); }
        else if (e.Key == Key.Space && shift) { e.Handled = true; SelectCurrentRow(); }
        else if (e.Key is Key.Delete or Key.Back)
        {
            e.Handled = true;
            if (control) DeleteRows(); else ClearSelectedCells();
        }
        else if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End or Key.Tab)
        {
            e.Handled = true;
            switch (e.Key)
            {
                case Key.Up: row = control ? 0 : row - 1; break;
                case Key.Down: row = control ? _visible.Count - 1 : row + 1; break;
                case Key.Left: col = control ? 0 : col - 1; break;
                case Key.Right: col = control ? _headers.Count - 1 : col + 1; break;
                case Key.Home: col = 0; if (control) row = 0; break;
                case Key.End: col = _headers.Count - 1; if (control) row = _visible.Count - 1; break;
                case Key.Tab:
                    col += shift ? -1 : 1;
                    if (col >= _headers.Count) { col = 0; row++; }
                    else if (col < 0) { col = _headers.Count - 1; row--; }
                    break;
            }
            SelectCell(row, col, shift && e.Key != Key.Tab);
            ScrollToCell();
        }
        else if (e.Key is Key.F2 or Key.Enter)
        {
            e.Handled = true;
            SelectCell(row, col);
            Table.BeginEdit();
        }
        else if (e.Key == Key.Escape && _cells.HasSelection)
        {
            e.Handled = true;
            SelectCell(_cells.AnchorRow, _cells.AnchorColumn);
        }
    }

    private void OnSelectionTextInput(object? sender, TextInputEventArgs e)
    {
        if (_editRow != null || string.IsNullOrEmpty(e.Text) || !_cells.HasSelection ||
            e.Source is not Control source || (source != Table && !source.GetVisualAncestors().Contains(Table))) return;
        e.Handled = true;
        SelectCell(_cells.Row, _cells.Column);
        if (Table.BeginEdit() && Table.Columns[_cells.Column].GetCellContent(_visible[_cells.Row]) is TextBox editor)
        {
            editor.Text = e.Text;
            editor.CaretIndex = editor.Text.Length;
        }
    }

    private string SelectedCellsText()
    {
        if (!_cells.HasSelection) return "";
        static string Escape(string s) => s.IndexOfAny(['\t', '\r', '\n', '"']) >= 0
            ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        return string.Join(Environment.NewLine, Enumerable.Range(_cells.Top, _cells.Bottom - _cells.Top + 1)
            .Select(r => string.Join('\t', Enumerable.Range(_cells.Left, _cells.Right - _cells.Left + 1)
                .Select(c => Escape(_visible[r].Cells[c])))));
    }

    private async Task<bool> CopyCellsAsync()
    {
        if (!_cells.HasSelection || Clipboard == null) return false;
        try { await Clipboard.SetTextAsync(SelectedCellsText()); return true; }
        catch (Exception ex) { StatusText.Text = $"Could not copy cells: {ex.Message}"; return false; }
    }

    private void ClearSelectedCells() => TransformSelectedCells("Clear Cells", _ => "");

    private void TransformSelectedCells(string name, Func<string, string> transform)
    {
        if (!Table.CommitEdit()) return;
        if (!_cells.HasSelection) { StatusText.Text = "Select the cells to change first."; return; }
        var changes = new List<(Row Row, int Col, string Old, string New)>();
        for (int r = _cells.Top; r <= _cells.Bottom; r++)
            for (int c = _cells.Left; c <= _cells.Right; c++)
            {
                var value = _visible[r].Cells[c];
                var transformed = transform(value);
                if (value != transformed) changes.Add((_visible[r], c, value, transformed));
            }
        ApplyCellChanges(name, changes);
        if (changes.Count == 0) StatusText.Text = $"{name}: no cells changed.";
    }

    private async Task PasteCellsAsync()
    {
        if (!Table.CommitEdit() || !_cells.HasSelection || Clipboard == null) return;
        string? text;
        try { text = await Clipboard.GetTextAsync(); }
        catch (Exception ex) { StatusText.Text = $"Could not paste cells: {ex.Message}"; return; }
        if (text != null) PasteCellsText(text);
    }

    private void PasteCellsText(string text)
    {
        if (!Table.CommitEdit() || !_cells.HasSelection) return;
        var records = Csv.Parse(text, '\t');
        if (records.Count == 0) return;
        int top = _cells.Top, left = _cells.Left;
        int height = records.Count == 1 && records[0].Length == 1 ? _cells.Bottom - top + 1 : records.Count;
        int width = records.Count == 1 && records[0].Length == 1 ? _cells.Right - left + 1 : records.Max(r => r.Length);
        if (top + height > _visible.Count || left + width > _headers.Count)
        {
            StatusText.Text = "Paste exceeds the table. Insert rows or columns first, then paste again.";
            return;
        }
        var changes = new List<(Row Row, int Col, string Old, string New)>();
        for (int r = 0; r < height; r++)
            for (int c = 0; c < width; c++)
            {
                string value = records.Count == 1 && records[0].Length == 1 ? records[0][0] :
                    c < records[r].Length ? records[r][c] : "";
                var row = _visible[top + r];
                if (row.Cells[left + c] != value) changes.Add((row, left + c, row.Cells[left + c], value));
            }
        ApplyCellChanges("Paste Cells", changes);
    }

    private void ApplyCellChanges(string name, List<(Row Row, int Col, string Old, string New)> changes)
    {
        if (changes.Count == 0) return;
        void Apply(bool undo)
        {
            foreach (var (row, col, old, value) in changes) row.Cells[col] = undo ? old : value;
            if (DuplicateFilterActive || !CurrentSearch().IsEmpty) ApplyFilter();
        }
        Apply(false);
        Push(new UndoEntry(name, () => Apply(true), () => Apply(false), changes.Count));
        StatusText.Text = $"{name}: {changes.Count:N0} cells.";
    }
}
