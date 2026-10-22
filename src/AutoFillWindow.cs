using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TableEdit.Model;

namespace TableEdit;

public enum FillScope { AllRows, EmptyCells, SelectedRows, VisibleRows }

/// <param name="Column">Index of the column to fill, or -1 to add a new column named NewColumnName.</param>
public sealed record FillRequest(int Column, string NewColumnName, FillPattern Pattern, FillScope Scope, int AddRows);

/// <summary>Dialog for Auto-Fill Column: pick a column, a preset or pattern, see a live preview, then fill.</summary>
public sealed class AutoFillWindow : Window
{
    private readonly ComboBox _column = new() { MinWidth = 220 };
    private readonly TextBox _newName = new() { Watermark = "New column name", Width = 200, IsVisible = false };
    private readonly ComboBox _preset = new() { MinWidth = 220, PlaceholderText = "Choose a preset…" };
    private readonly TextBox _pattern = new() { Watermark = "e.g. (%n3%) %n3%-%n4%", FontFamily = new FontFamily("Menlo, Consolas, monospace") };
    private readonly ComboBox _scope = new() { MinWidth = 220, SelectedIndex = 0 };
    private readonly NumericUpDown _addRows = new() { Minimum = 0, Maximum = 1_000_000, Value = 0, Increment = 10, FormatString = "0", Width = 150 };
    private readonly TextBlock _preview = new() { FontFamily = new FontFamily("Menlo, Consolas, monospace"), Opacity = 0.85 };
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
    private readonly Button _fill = new() { Content = "Fill", IsDefault = true };
    private readonly IReadOnlyList<string> _headers;

    public AutoFillWindow(IReadOnlyList<string> headers, int currentColumn, bool hasSelection)
    {
        _headers = headers;
        Title = "Auto-Fill Column";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _column.ItemsSource = headers.Concat(new[] { "＋ New column…" }).ToList();
        _column.SelectedIndex = currentColumn >= 0 ? currentColumn : headers.Count;
        _preset.ItemsSource = FillPattern.Presets.Select(p => $"{p.Label}    {p.Pattern}").ToList();
        _scope.ItemsSource = new[] { "All rows", "Only empty cells", "Selected rows", "Rows shown by search" };
        _scope.SelectedIndex = hasSelection ? 2 : 0;

        _column.SelectionChanged += (_, _) =>
        {
            _newName.IsVisible = _column.SelectedIndex == _headers.Count;
            UpdatePreview();
        };
        _preset.SelectionChanged += (_, _) =>
        {
            if (_preset.SelectedIndex >= 0) _pattern.Text = FillPattern.Presets[_preset.SelectedIndex].Pattern;
        };
        _pattern.TextChanged += (_, _) => UpdatePreview();
        _newName.IsVisible = _column.SelectedIndex == _headers.Count;

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        _fill.Click += (_, _) => Submit();

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children =
            {
                Row("Column", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _column, _newName } }),
                Row("Preset", _preset),
                Row("Pattern", _pattern),
                new TextBlock { Text = FillPattern.Help, FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(96, 0, 0, 0) },
                Row("Fill", _scope),
                Row("Add rows", new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { _addRows, new TextBlock { Text = "new rows at the end, filled too", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.65 } },
                }),
                Row("Preview", new Border
                {
                    Padding = new Thickness(10, 6),
                    CornerRadius = new CornerRadius(4),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
                    Child = _preview,
                }),
                _error,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, _fill },
                },
            },
        };

        Opened += (_, _) => _pattern.Focus();
        UpdatePreview();
    }

    private static Grid Row(string label, Control content)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("96,*") };
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 });
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        return grid;
    }

    private FillPattern? Parse()
    {
        var text = _pattern.Text ?? "";
        if (text.Length == 0)
        {
            _error.Text = "";
            return null;
        }
        var pattern = FillPattern.TryParse(text, out var error);
        if (pattern == null)
        {
            _error.Text = error;
            return null;
        }
        var missing = pattern.ReferencedColumns.Where(c => !_headers.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        _error.Text = missing.Count > 0 ? "No column named: " + string.Join(", ", missing) + " (it will be left blank)" : "";
        return pattern;
    }

    private void UpdatePreview()
    {
        var pattern = Parse();
        _fill.IsEnabled = pattern != null;
        if (pattern == null)
        {
            _preview.Text = "—";
            return;
        }
        var rng = new Random();
        _preview.Text = string.Join("\n", Enumerable.Range(1, 5).Select(i => pattern.Generate(rng, i, name => $"<{name}>")));
    }

    private void Submit()
    {
        var pattern = Parse();
        if (pattern == null) return;
        bool isNew = _column.SelectedIndex == _headers.Count;
        var name = (_newName.Text ?? "").Trim();
        if (isNew && name.Length == 0)
        {
            _error.Text = "Enter a name for the new column.";
            _newName.Focus();
            return;
        }
        Close(new FillRequest(isNew ? -1 : _column.SelectedIndex, name, pattern, (FillScope)_scope.SelectedIndex, (int)(_addRows.Value ?? 0)));
    }
}
