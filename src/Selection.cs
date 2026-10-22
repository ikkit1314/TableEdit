using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TableEdit.Model;

namespace TableEdit;

public partial class MainWindow
{
    private readonly HashSet<Row> _selectedCellRows = new();
    private int _selectionColumn = -1;
    private Row? _selectionAnchor;
    private int _selectionAnchorColumn = -1;
    private string[]? _duplicateReference;

    private void WireSelection()
    {
        _highlight.IsSelected = (row, col) => col == _selectionColumn && _selectedCellRows.Contains(row);
        Table.AddHandler(PointerPressedEvent, (_, e) =>
        {
            // Scrolling and resizing should preserve the selected cells.
            if (e.Source is Control source &&
                (source is DataGridCell or DataGridRowHeader ||
                 source.GetVisualAncestors().Any(x => x is DataGridCell or DataGridRowHeader)))
            {
                ClearCellSelection();
                UpdateCellStatus();
            }
        }, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnSelectionKeyDown, RoutingStrategies.Tunnel);
        SelectRowButton.Click += (_, _) => SelectCurrentRow();
        SelectColumnButton.Click += (_, _) => SelectColumn(CurrentCol);
        FindDuplicatesButton.Click += (_, _) => FindSelectedRowDuplicates();
        ShowAllButton.Click += (_, _) => ClearFilter();
    }

    private void ClearCellSelection()
    {
        if (_selectionColumn < 0 && _selectedCellRows.Count == 0) return;
        _selectionColumn = -1;
        _selectedCellRows.Clear();
        _highlight.Refresh();
    }

    private void SelectCurrentRow()
    {
        if (!Table.CommitEdit()) return;
        var row = Table.SelectedItem as Row ?? _selectionAnchor;
        ClearCellSelection();
        if (row == null || !_visible.Contains(row)) return;
        Table.SelectedItems.Clear();
        Table.SelectedItem = null;
        Table.SelectedItem = row;
        Table.Focus();
        UpdateCellStatus();
    }

    private void SelectColumn(int column, bool fromCurrent = false, bool upwards = false)
    {
        if (column < 0 || column >= _headers.Count || _visible.Count == 0 || !Table.CommitEdit()) return;
        var anchor = Table.SelectedItem as Row ?? _selectionAnchor;
        int start = fromCurrent && anchor != null ? _visible.IndexOf(anchor) : 0;
        if (start < 0) start = 0;
        int end = upwards ? start : _visible.Count - 1;
        if (upwards) start = 0;
        _selectionAnchor = anchor ?? _visible[0];
        _selectionAnchorColumn = column;
        _selectionColumn = column;
        _selectedCellRows.Clear();
        for (int i = start; i <= end; i++) _selectedCellRows.Add(_visible[i]);
        Table.SelectedItems.Clear();
        Table.CurrentColumn = Table.Columns[column];
        _highlight.Refresh();
        if (fromCurrent) Table.ScrollIntoView(_visible[upwards ? start : end], Table.Columns[column]);
        Table.Focus();
        UpdateCellStatus();
    }

    private async void OnSelectionKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is not Control source || (source != Table && !source.GetVisualAncestors().Contains(Table))) return;
        // Leave ordinary cursor movement and text selection to the active editor.
        if (_editRow != null) return;
        bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(Cmd);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (control && shift && (e.Key == Key.Down || e.Key == Key.Up))
        {
            int col = CurrentCol >= 0 ? CurrentCol : _selectionAnchorColumn;
            SelectColumn(col, fromCurrent: true, upwards: e.Key == Key.Up);
            e.Handled = true;
        }
        else if (e.Key == Key.Space && control && !shift)
        {
            SelectColumn(CurrentCol);
            e.Handled = true;
        }
        else if (e.Key == Key.Space && shift && !control)
        {
            SelectCurrentRow();
            e.Handled = true;
        }
        else if (control && e.Key == Key.C && _selectionColumn >= 0)
        {
            e.Handled = true;
            var text = SelectedColumnText();
            if (Clipboard != null) await Clipboard.SetTextAsync(text);
        }
        else if (e.Key == Key.Escape && _selectionColumn >= 0)
        {
            SelectCurrentRow();
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right && _selectionColumn >= 0)
        {
            SelectCurrentRow();
        }
    }

    private string SelectedColumnText()
    {
        int col = _selectionColumn;
        if (col < 0) return "";
        static string Escape(string s) => s.IndexOfAny(['\t', '\r', '\n', '"']) >= 0
            ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        return string.Join(Environment.NewLine,
            _visible.Where(_selectedCellRows.Contains).Select(r => Escape(r.Cells[col])));
    }

    private void FindSelectedRowDuplicates()
    {
        if (!Table.CommitEdit()) return;
        var reference = Table.SelectedItem as Row ?? _selectionAnchor;
        if (reference == null || !_rows.Contains(reference))
        {
            StatusText.Text = "Select a row to find its duplicates.";
            return;
        }
        var values = reference.ToArray();
        int count = _rows.Count(r => r.Cells.SequenceEqual(values, StringComparer.Ordinal));
        if (count < 2)
        {
            StatusText.Text = "No duplicates of the selected row (all columns, exact match).";
            return;
        }
        ClearFilter();
        _duplicateReference = values;
        ApplyFilter();
        Table.SelectedItem = reference;
        StatusText.Text = $"Found {count - 1:N0} duplicate(s) plus the selected row · all columns, exact match · Show All to return.";
    }
}
