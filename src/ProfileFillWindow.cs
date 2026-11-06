using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TableEdit.Model;

namespace TableEdit;

/// <param name="Scope">AllRows, SelectedRows or VisibleRows (EmptyCells is expressed by OnlyEmpty).</param>
/// <param name="AddStandardColumns">Add the usual PROFILE_NAME;EMAIL;… columns that the table is missing.</param>
public sealed record ProfileFillRequest(FillScope Scope, bool OnlyEmpty, int AddRows, bool AddStandardColumns, ProfileOptions Options);

/// <summary>Dialog for Data › Fill Profiles with Random Data: shows which columns were recognised, then fills them.</summary>
public sealed class ProfileFillWindow : Window
{
    public static readonly string[] StandardColumns =
    {
        "PROFILE_NAME", "EMAIL", "PHONE",
        "SHIPPING_FIRST_NAME", "SHIPPING_LAST_NAME", "SHIPPING_ADDRESS", "SHIPPING_ADDRESS_2", "SHIPPING_CITY",
        "SHIPPING_ZIP", "SHIPPING_STATE", "SHIPPING_COUNTRY",
        "BILLING_FIRST_NAME", "BILLING_LAST_NAME", "BILLING_ADDRESS", "BILLING_ADDRESS_2", "BILLING_CITY",
        "BILLING_ZIP", "BILLING_STATE", "BILLING_COUNTRY", "BILLING_SAME_AS_SHIPPING",
        "CARD_HOLDER_NAME", "CARD_TYPE", "CARD_NUMBER", "CARD_MONTH", "CARD_YEAR", "CARD_CVV", "ONE_CHECKOUT_PER_PROFILE",
    };

    private readonly ComboBox _scope = new() { MinWidth = 220 };
    private readonly CheckBox _onlyEmpty = new() { Content = "Only fill empty cells (keep values already there)", IsChecked = true };
    private readonly ComboBox _country = new() { MinWidth = 220, SelectedIndex = 0 };
    private readonly CheckBox _codes = new() { Content = "Short codes (US, NY) instead of full names", IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
    private readonly CheckBox _billingSame = new() { Content = "Billing same as shipping", IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
    private readonly CheckBox _fourDigitYear = new() { Content = "4-digit card year (2029 instead of 29)", IsChecked = false };
    private readonly CheckBox _addColumns = new() { Content = "Add the standard profile columns the table is missing" };
    private readonly NumericUpDown _addRows = new() { Minimum = 0, Maximum = 1_000_000, Value = 0, Increment = 10, FormatString = "0", Width = 150 };
    private readonly TextBlock _preview = new() { FontFamily = new FontFamily("Menlo, Consolas, monospace"), Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
    private readonly Button _fill = new() { Content = "Fill", IsDefault = true };
    private readonly IReadOnlyList<string> _headers;
    private readonly List<ProfileColumn> _detected;

    public ProfileFillWindow(IReadOnlyList<string> headers, bool hasSelection, bool searching)
    {
        _headers = headers;
        _detected = ProfileFill.Detect(headers);
        Title = "Fill Profiles with Random Data";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _scope.ItemsSource = new[] { "All rows", "Selected rows", "Rows shown by search" };
        _scope.SelectedIndex = hasSelection ? 1 : searching ? 2 : 0;
        _country.ItemsSource = new[] { "United States", "Canada" };
        _country.SelectedIndex = 0;
        var missing = StandardColumns.Where(s => !headers.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
        _addColumns.IsVisible = missing.Count > 0;
        _addColumns.IsChecked = _detected.Count == 0;

        foreach (var box in new[] { _onlyEmpty, _codes, _billingSame, _fourDigitYear, _addColumns })
            box.IsCheckedChanged += (_, _) => UpdatePreview();
        _country.SelectionChanged += (_, _) => UpdatePreview();

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        _fill.Click += (_, _) => Submit();

        var recognised = _detected.Count == 0
            ? "No profile columns recognised yet."
            : string.Join("\n", _detected.Select(c => $"{c.Header}  →  {ProfileFill.Describe(c.Field)}"));
        var ignored = headers.Where((h, i) => _detected.All(c => c.Index != i)).ToList();
        if (ignored.Count > 0) recognised += "\n\nLeft alone: " + string.Join(", ", ignored);

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children =
            {
                Row("Columns", new ScrollViewer
                {
                    MaxHeight = 110,
                    Content = new TextBlock { Text = recognised, FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap },
                }),
                Row("", _addColumns),
                Row("Fill", _scope),
                Row("", _onlyEmpty),
                Row("Addresses", _country),
                Row("", new WrapPanel { Children = { _codes, _billingSame, _fourDigitYear } }),
                Row("Add rows", new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { _addRows, new TextBlock { Text = "new profiles at the end", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.65 } },
                }),
                Row("Example", new Border
                {
                    Padding = new Thickness(10, 6),
                    CornerRadius = new CornerRadius(4),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
                    Child = _preview,
                }),
                new TextBlock
                {
                    Text = "Card numbers are public test numbers (4242…, 5555…), not real cards. Replace them with your own before use.",
                    FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(96, 0, 0, 0),
                },
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

    private ProfileOptions Options() => new(
        Canada: _country.SelectedIndex == 1,
        Codes: _codes.IsChecked == true,
        BillingSameAsShipping: _billingSame.IsChecked == true,
        FourDigitYear: _fourDigitYear.IsChecked == true);

    /// <summary>The headers the table will have after filling, so the example matches what Fill does.</summary>
    private List<string> ResultHeaders() => _addColumns.IsChecked == true
        ? _headers.Concat(StandardColumns.Where(s => !_headers.Contains(s, StringComparer.OrdinalIgnoreCase))).ToList()
        : _headers.ToList();

    private void UpdatePreview()
    {
        var headers = ResultHeaders();
        var columns = ProfileFill.Detect(headers);
        _fill.IsEnabled = columns.Count > 0;
        _error.Text = columns.Count == 0
            ? "None of the column names look like profile fields (EMAIL, PHONE, SHIPPING_CITY, Postal Code…). Tick the box above to add them."
            : "";
        if (columns.Count == 0) { _preview.Text = "—"; return; }
        var values = ProfileFill.Generate(new Random(), columns, _ => "", false, Options());
        _preview.Text = string.Join("\n", columns.Take(6).Select(c => $"{c.Header}: {values[c.Index]}"))
            + (columns.Count > 6 ? $"\n… and {columns.Count - 6} more" : "");
    }

    private void Submit()
    {
        if (!_fill.IsEnabled) return;
        var scope = _scope.SelectedIndex switch { 1 => FillScope.SelectedRows, 2 => FillScope.VisibleRows, _ => FillScope.AllRows };
        Close(new ProfileFillRequest(scope, _onlyEmpty.IsChecked == true, (int)(_addRows.Value ?? 0),
            _addColumns.IsVisible && _addColumns.IsChecked == true, Options()));
    }
}
