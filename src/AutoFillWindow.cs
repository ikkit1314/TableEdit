using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TableEdit.Model;

namespace TableEdit;

public enum FillScope { AllRows, EmptyCells, SelectedRows, VisibleRows }

/// <param name="Column">Index of the column to fill, or -1 for the new column named in the request.</param>
public sealed record ColumnFill(int Column, FillPattern Pattern);

/// <param name="Canada">Address tokens (%city% %state% %zip% %phone%) use Canadian places instead of US ones.</param>
public sealed record FillRequest(IReadOnlyList<ColumnFill> Columns, string NewColumnName, FillScope Scope, int AddRows, bool Canada = false)
{
    public FillRequest(int column, string newColumnName, FillPattern pattern, FillScope scope, int addRows)
        : this(new[] { new ColumnFill(column, pattern) }, newColumnName, scope, addRows) { }
}

/// <summary>
/// Dialog for Auto-Fill: every column gets a pattern guessed from its name (SHIPPING_ZIP → %zip%). Tick as many
/// columns as you like; each row is filled with one random person across all of them.
/// </summary>
public sealed class AutoFillWindow : Window
{
    private sealed record Line(CheckBox Tick, TextBox Pattern, int Column);

    private readonly List<Line> _lines = new();
    private readonly TextBox _newName = new() { Watermark = "New column name", Width = 180 };
    private readonly ComboBox _preset = new() { MinWidth = 260, PlaceholderText = "Insert a preset into the selected pattern…" };
    private readonly ComboBox _scope = new() { MinWidth = 220 };
    private readonly CheckBox _canada = new() { Content = "Canadian addresses (city, province, postal code, phone)" };
    private readonly NumericUpDown _addRows = new() { Minimum = 0, Maximum = 1_000_000, Value = 0, Increment = 10, FormatString = "0", Width = 150 };
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
    private readonly TextBlock _preview = new() { FontFamily = new FontFamily("Menlo, Consolas, monospace"), FontSize = 12, Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
    private readonly Button _fill = new() { Content = "Fill", IsDefault = true };
    private readonly IReadOnlyList<string> _headers;
    private TextBox? _focused;

    /// <param name="ticked">Columns to tick at the start, normally the selected ones.</param>
    public AutoFillWindow(IReadOnlyList<string> headers, IReadOnlyCollection<int> ticked, bool hasSelection)
    {
        _headers = headers;
        Title = "Auto-Fill Columns";
        Width = 720;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var list = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*") };
        for (int i = 0; i <= headers.Count; i++)
        {
            bool isNew = i == headers.Count;
            var tick = new CheckBox
            {
                // A TextBlock, not a string: Avalonia would treat the first "_" in SHIPPING_ZIP as an access key.
                Content = new TextBlock { Text = isNew ? "＋ New column" : headers[i], TextTrimming = TextTrimming.CharacterEllipsis },
                IsChecked = !isNew && ticked.Contains(i),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(tick, isNew ? "Add a column on the right and fill it" : headers[i]);
            var pattern = new TextBox
            {
                Text = isNew ? "" : FillPattern.Suggest(headers[i]) ?? "",
                Watermark = isNew ? "pattern for the new column" : "no match: type a pattern or pick a preset",
                Margin = new Thickness(0, 2),
                FontFamily = new FontFamily("Menlo, Consolas, monospace"),
            };
            var line = new Line(tick, pattern, isNew ? -1 : i);
            _lines.Add(line);
            pattern.GotFocus += (_, _) => _focused = pattern;
            pattern.TextChanged += (_, _) =>
            {
                // Typing a pattern means "fill this column".
                if (pattern.IsFocused && !string.IsNullOrEmpty(pattern.Text)) tick.IsChecked = true;
                Refresh();
            };
            tick.IsCheckedChanged += (_, _) => Refresh();

            list.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Control left = tick;
            if (isNew)
            {
                left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { tick, _newName } };
                _newName.TextChanged += (_, _) => { if (_newName.IsFocused && !string.IsNullOrEmpty(_newName.Text)) tick.IsChecked = true; Refresh(); };
            }
            Grid.SetRow(left, i);
            Grid.SetRow(pattern, i);
            Grid.SetColumn(pattern, 1);
            list.Children.Add(left);
            list.Children.Add(pattern);
        }

        _preset.ItemsSource = FillPattern.Presets.Select(p => $"{p.Label}    {p.Pattern}").ToList();
        _preset.SelectionChanged += (_, _) =>
        {
            if (_preset.SelectedIndex < 0) return;
            var target = _focused ?? _lines.FirstOrDefault(l => l.Tick.IsChecked == true)?.Pattern;
            if (target != null)
            {
                target.Text = FillPattern.Presets[_preset.SelectedIndex].Pattern;
                _lines.First(l => l.Pattern == target).Tick.IsChecked = true;
                target.Focus();
            }
            _preset.SelectedIndex = -1;
        };
        _scope.ItemsSource = new[] { "All rows", "Only empty cells", "Selected rows", "Rows shown by search" };
        _scope.SelectedIndex = hasSelection ? 2 : 0;
        _canada.IsCheckedChanged += (_, _) => Refresh();

        var matchAll = new Button { Content = "Tick All Matched" };
        ToolTip.SetTip(matchAll, "Tick every column whose name was recognised and has a pattern");
        matchAll.Click += (_, _) =>
        {
            foreach (var l in _lines) if (l.Column >= 0 && !string.IsNullOrWhiteSpace(l.Pattern.Text)) l.Tick.IsChecked = true;
        };
        var clear = new Button { Content = "Untick All" };
        clear.Click += (_, _) => { foreach (var l in _lines) l.Tick.IsChecked = false; };

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        _fill.Click += (_, _) => Submit();

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { matchAll, clear, _count } },
                new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
                    Padding = new Thickness(8, 6),
                    Child = new ScrollViewer { MaxHeight = 300, Content = list },
                },
                Row("Preset", _preset),
                new TextBlock { Text = FillPattern.Help, FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap },
                Row("Fill", _scope),
                Row("", _canada),
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

        Opened += (_, _) => (_lines.FirstOrDefault(l => l.Tick.IsChecked == true) ?? _lines[0]).Pattern.Focus();
        Refresh();
    }

    private static Grid Row(string label, Control content)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("80,*") };
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 });
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        return grid;
    }

    private string LineName(Line l) => l.Column >= 0 ? _headers[l.Column] : string.IsNullOrWhiteSpace(_newName.Text) ? "New column" : _newName.Text.Trim();

    /// <summary>Parses every ticked pattern; returns null and shows why when one can't be used.</summary>
    private List<(Line Line, FillPattern Pattern)>? Parse()
    {
        var parsed = new List<(Line, FillPattern)>();
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in _lines.Where(l => l.Tick.IsChecked == true))
        {
            var text = l.Pattern.Text ?? "";
            if (text.Length == 0)
            {
                _error.Text = $"{LineName(l)}: type a pattern or pick a preset, or untick it.";
                return null;
            }
            var pattern = FillPattern.TryParse(text, out var error);
            if (pattern == null)
            {
                _error.Text = $"{LineName(l)}: {error}";
                return null;
            }
            foreach (var c in pattern.ReferencedColumns.Where(c => !_headers.Contains(c, StringComparer.OrdinalIgnoreCase))) missing.Add(c);
            parsed.Add((l, pattern));
        }
        _error.Text = missing.Count > 0 ? "No column named: " + string.Join(", ", missing) + " (it will be left blank)" : "";
        return parsed;
    }

    private void Refresh()
    {
        var parsed = Parse();
        int ticked = _lines.Count(l => l.Tick.IsChecked == true);
        _count.Text = ticked == 1 ? "1 column ticked" : $"{ticked} columns ticked";
        _fill.IsEnabled = parsed is { Count: > 0 };
        if (parsed == null || parsed.Count == 0)
        {
            _preview.Text = "—";
            return;
        }
        // Three example rows; every column in a row shares one random person, as when filling.
        var rng = new Random();
        var rows = Enumerable.Range(1, 3).Select(i =>
        {
            var sample = ProfileFill.Sample.Random(rng, _canada.IsChecked == true);
            return parsed.Select(p => p.Pattern.Generate(rng, i, name => $"<{name}>", sample)).ToList();
        }).ToList();
        const int shown = 8;
        _preview.Text = string.Join("\n", parsed.Take(shown).Select((p, c) =>
                $"{LineName(p.Line)}:  {string.Join("  ·  ", rows.Select(r => r[c]))}"))
            + (parsed.Count > shown ? $"\n… and {parsed.Count - shown} more columns" : "");
    }

    private void Submit()
    {
        var parsed = Parse();
        if (parsed is not { Count: > 0 }) return;
        var name = (_newName.Text ?? "").Trim();
        if (parsed.Any(p => p.Line.Column < 0) && name.Length == 0)
        {
            _error.Text = "Enter a name for the new column.";
            _newName.Focus();
            return;
        }
        Close(new FillRequest(parsed.Select(p => new ColumnFill(p.Line.Column, p.Pattern)).ToList(), name,
            (FillScope)_scope.SelectedIndex, (int)(_addRows.Value ?? 0), _canada.IsChecked == true));
    }
}
