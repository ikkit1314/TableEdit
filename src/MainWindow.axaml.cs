using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TableEdit.Model;

namespace TableEdit;

public partial class MainWindow : Window
{
    /// <param name="Cost">Approximate cells held by the entry, used to cap undo memory on big files.</param>
    private sealed record UndoEntry(string Name, Action Undo, Action Redo, long Cost = 0);

    private sealed class RelayCommand(Action run) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run();
    }

    private const int MaxUndo = 50;
    private const long MaxUndoCells = 20_000_000;
    private static readonly List<MainWindow> OpenWindows = new();

    private List<string> _headers = new();
    private List<Row> _rows = new();
    private ObservableCollection<Row> _visible = new();

    private string? _path;
    private string? _loadingPath;
    private int _loadGeneration;
    private bool _isClosed;
    private char _delimiter = ',';
    private Encoding _encoding = new UTF8Encoding(false);
    private string _newLine = "\n";
    private bool _hasHeader = true;
    private bool _dirty;
    private bool _forceClose;
    private bool _suppressFilter;

    private int _sortColumn = -1;
    private bool _sortAscending = true;

    private readonly List<UndoEntry> _undo = new();
    private readonly List<UndoEntry> _redo = new();

    private Row? _editRow;
    private int _editCol = -1;
    private string _editOld = "";

    private readonly DispatcherTimer _filterTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private CancellationTokenSource? _filterCts;
    private int _filterGeneration;
    private FindReplaceWindow? _findWindow;
    private readonly SearchHighlight _highlight = new();

    private NativeMenuItem _undoItem = null!;
    private NativeMenuItem _redoItem = null!;
    private NativeMenuItem _headerToggle = null!;
    private readonly NativeMenu _windowMenu = new();

    public MainWindow()
    {
        InitializeComponent();
        OpenWindows.Add(this);
        Closed += (_, _) =>
        {
            _isClosed = true;
            OpenWindows.Remove(this);
            RefreshWindowMenus();
        };
        Closing += OnClosing;

        BuildMenu();
        WireGrid();

        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            ApplyFilter();
        };
        FilterBox.TextChanged += (_, _) =>
        {
            ClearFilterButton.IsVisible = !string.IsNullOrEmpty(FilterBox.Text);
            _filterTimer.Stop();
            // TextChanged can arrive after ClearFilter has already applied the empty query.
            // Do not schedule a second refresh that would erase the user's new cell selection.
            if (_suppressFilter || !CurrentQueryChanged()) return;
            _filterTimer.Start();
        };
        FilterBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                ClearFilter();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                // Enter / Shift+Enter step through matching cells, like Find Next / Previous.
                _filterTimer.Stop();
                if (_highlight.Search == null || CurrentQueryChanged()) ApplyFilter();
                JumpToMatch(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                e.Handled = true;
            }
            else if (e.Key == Key.Down && _visible.Count > 0)
            {
                SelectCell(0, Math.Max(0, CurrentCol));
                Table.ScrollIntoView(_visible[0], Table.CurrentColumn);
                Table.Focus();
                e.Handled = true;
            }
        };
        FilterColumn.SelectionChanged += (_, _) => { if (!_suppressFilter) ApplyFilter(); };
        FilterCase.IsCheckedChanged += (_, _) => ApplyFilter();
        FilterInvert.IsCheckedChanged += (_, _) => ApplyFilter();
        FilterMode.SelectionChanged += (_, _) => { if (!_suppressFilter) ApplyFilter(); };
        NextMatchButton.Click += (_, _) => JumpToMatch(1);
        PrevMatchButton.Click += (_, _) => JumpToMatch(-1);
        ClearFilterButton.Click += (_, _) => ClearFilter();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        NewDocument(3, 1);
        _dirty = false;
        UpdateTitle();
    }

    // ───────────────────────────── menus ─────────────────────────────

    private static KeyModifiers Cmd => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    private NativeMenuItem Item(string header, Action action, Key? key = null, KeyModifiers extra = KeyModifiers.None)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        if (key is { } k)
        {
            var gesture = new KeyGesture(k, Cmd | extra);
            item.Gesture = gesture;
            // The macOS menu bar handles key equivalents natively; elsewhere bind them on the window.
            if (!OperatingSystem.IsMacOS())
                KeyBindings.Add(new KeyBinding { Gesture = gesture, Command = new RelayCommand(action) });
        }
        return item;
    }

    private static NativeMenuItem Sub(string header, params NativeMenuItemBase[] items)
    {
        var menu = new NativeMenu();
        foreach (var i in items) menu.Items.Add(i);
        return new NativeMenuItem(header) { Menu = menu };
    }

    private static NativeMenuItemSeparator Sep() => new();

    private void BuildMenu()
    {
        _undoItem = Item("Undo", Undo, Key.Z);
        _redoItem = Item("Redo", Redo, Key.Z, KeyModifiers.Shift);
        _headerToggle = new NativeMenuItem("First Row Is Header") { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = true };
        _headerToggle.Click += (_, _) => ToggleHeader();

        var export = Exporters.All.Select(f => (NativeMenuItemBase)Item(f.Name + "…", () => _ = ExportAsync(f))).ToArray();
        var reopen = new NativeMenuItemBase[] { Item("Auto-Detect", () => _ = ReopenAsync(null)), Sep() }
            .Concat(Csv.CandidateDelimiters.Select(d => (NativeMenuItemBase)Item(Csv.DelimiterName(d), () => _ = ReopenAsync(d))))
            .ToArray();

        var menu = new NativeMenu
        {
            Sub("File",
                Item("New Window", () => new MainWindow().Show(), Key.N),
                Item("Open…", () => _ = OpenAsync(), Key.O),
                Sep(),
                Item("Save", () => _ = SaveAsync(false), Key.S),
                Item("Save As…", () => _ = SaveAsync(true), Key.S, KeyModifiers.Shift),
                Sub("Export Visible Rows", export),
                Sub("Reopen with Delimiter", reopen),
                Sep(),
                Item("Close Window", Close, Key.W)),
            Sub("Edit",
                _undoItem,
                _redoItem,
                Sep(),
                Item("Search Rows", FocusSearch, Key.F),
                Item("Find Next Match", () => JumpToMatch(1), Key.G),
                Item("Find Previous Match", () => JumpToMatch(-1), Key.G, KeyModifiers.Shift),
                Item("Clear Search", ClearFilter),
                Item("Find & Replace…", ShowFind, Key.F, KeyModifiers.Alt),
                Sep(),
                Item("Insert Row Below", InsertRow, Key.Enter),
                Item("Duplicate Selected Rows", DuplicateRows, Key.D),
                Item("Delete Selected Rows", DeleteRows, Key.Back),
                Item("Paste Rows from Clipboard", () => _ = PasteRowsAsync(), Key.V, KeyModifiers.Shift),
                Item("Select Current Row", SelectCurrentRow),
                Item("Select Current Column", () => SelectColumn(CurrentCol)),
                Item("Find Duplicates in Current Column", FindColumnDuplicates),
                Item("Select All Cells", SelectAllCells, Key.A, KeyModifiers.Shift)),
            Sub("Column",
                Item("Auto-Fill Column…", () => _ = AutoFillAsync(), Key.F, KeyModifiers.Shift),
                Sep(),
                Item("Insert Column…", () => _ = InsertColumnAsync()),
                Item("Rename Column…", () => _ = RenameColumnAsync(), Key.R, KeyModifiers.Shift),
                Item("Delete Column", DeleteColumn),
                Item("Move Column Left", () => MoveColumn(-1), Key.Left, KeyModifiers.Alt),
                Item("Move Column Right", () => MoveColumn(1), Key.Right, KeyModifiers.Alt),
                Sep(),
                Item("Sort Ascending (A→Z, 0→9)", () => _ = SortByAsync(CurrentCol, true)),
                Item("Sort Descending (Z→A, 9→0)", () => _ = SortByAsync(CurrentCol, false)),
                Item("Restore Original Order", () => _ = ClearSortAsync()),
                Sep(),
                Item("Column Statistics…", ShowStatistics, Key.I),
                Sep(),
                _headerToggle),
            Sub("Data",
                Item("Auto-Fill Column…", () => _ = AutoFillAsync()),
                Sub("Selected Cells",
                    Item("Trim Whitespace", () => TransformSelectedCells("Trim Selected Cells", s => s.Trim())),
                    Item("UPPERCASE", () => TransformSelectedCells("Uppercase Selected Cells", s => s.ToUpper(CultureInfo.CurrentCulture))),
                    Item("lowercase", () => TransformSelectedCells("Lowercase Selected Cells", s => s.ToLower(CultureInfo.CurrentCulture))),
                    Item("Title Case", () => TransformSelectedCells("Title Case Selected Cells",
                        s => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s.ToLower(CultureInfo.CurrentCulture))))),
                Sep(),
                Item("Trim Whitespace", TrimWhitespace),
                Item("Remove Empty Rows", RemoveEmptyRows),
                Item("Find Duplicates in Current Column", FindColumnDuplicates),
                Item("Remove Duplicate Rows", RemoveDuplicateRows),
                Item("Fill Empty Cells from Above (Current Column)", FillDown),
                Sep(),
                Item("UPPERCASE Current Column", () => ChangeCase("Uppercase", s => s.ToUpper(CultureInfo.CurrentCulture))),
                Item("lowercase Current Column", () => ChangeCase("Lowercase", s => s.ToLower(CultureInfo.CurrentCulture))),
                Item("Title Case Current Column", () => ChangeCase("Title Case", s => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s.ToLower(CultureInfo.CurrentCulture))))),
            new NativeMenuItem("Window") { Menu = _windowMenu },
        };
        NativeMenu.SetMenu(this, menu);
        UpdateUndoMenu();
        RefreshWindowMenus();
    }

    private static void RefreshWindowMenus()
    {
        foreach (var owner in OpenWindows)
        {
            owner._windowMenu.Items.Clear();
            foreach (var document in OpenWindows)
            {
                var item = new NativeMenuItem(document.Title ?? "Untitled")
                {
                    ToggleType = NativeMenuItemToggleType.CheckBox,
                    IsChecked = owner == document,
                    Command = new RelayCommand(() => ActivateDocument(document)),
                };
                owner._windowMenu.Items.Add(item);
            }
        }
    }

    private static void ActivateDocument(MainWindow window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        RefreshWindowMenus();
    }

    // ───────────────────────────── grid wiring ─────────────────────────────

    private int CurrentCol => _cells.HasSelection ? _cells.Column : Table.CurrentColumn is { } c ? Table.Columns.IndexOf(c) : -1;

    private void WireGrid()
    {
        WireSelection();
        Table.LoadingRow += (_, e) => e.Row.Header = (e.Row.Index + 1).ToString("N0", CultureInfo.CurrentCulture);

        Table.BeginningEdit += (_, e) =>
        {
            if (_typingTargets == null && _cells.HasSelection && e.EditingEventArgs is TextInputEventArgs)
                _typingTargets = CaptureTypingTargets();
            if (e.Row.DataContext is Row r)
            {
                _editRow = r;
                _editCol = Table.Columns.IndexOf(e.Column);
                _editOld = _editCol >= 0 && _editCol < r.Cells.Count ? r.Cells[_editCol] : "";
            }
        };

        Table.PreparingCellForEdit += (_, e) =>
        {
            if (e.EditingElement is TextBox box)
            {
                box.Focus();
                if (_typingTargets != null) box.SelectAll();
                else box.CaretIndex = box.Text?.Length ?? 0;
            }
        };

        Table.CellEditEnded += (_, e) =>
        {
            var r = _editRow;
            int c = _editCol;
            _editRow = null;
            var targets = _typingTargets;
            _typingTargets = null;
            if (r == null || c < 0 || c >= r.Cells.Count) return;
            if (e.EditAction == DataGridEditAction.Cancel)
            {
                // Template editors write through immediately, so Esc has to put the old text back.
                if (r.Cells[c] != _editOld) r.Cells[c] = _editOld;
                return;
            }

            r.Cells[c] ??= "";
            var oldValue = _editOld;
            var newValue = r.Cells[c];
            if (targets != null)
            {
                var changes = targets.Where(target => target.Old != newValue)
                    .Select(target => (target.Row, target.Col, target.Old, New: newValue)).ToList();
                ApplyCellChanges(targets.Count == 1 ? "Type Cell" : "Type Selected Cells", changes, deferFilter: true);
                return;
            }
            if (oldValue == newValue) return;
            ApplyCellChanges("Edit Cell", new() { (r, c, oldValue, newValue) }, deferFilter: true);
        };

        Table.Sorting += (_, e) =>
        {
            e.Handled = true;
            int col = Table.Columns.IndexOf(e.Column);
            ClearCellSelection();
            Table.CurrentColumn = e.Column;
            _ = SortByAsync(col, col != _sortColumn || !_sortAscending);
        };

        Table.CurrentCellChanged += (_, _) => UpdateCellStatus();
        Table.SelectionChanged += (_, _) => UpdateCellStatus();
    }

    private void RebuildColumns()
    {
        _searchColumnRevision++;
        ClearCellSelection();
        Table.Columns.Clear();
        for (int i = 0; i < _headers.Count; i++)
        {
            Table.Columns.Add(MakeColumn(i));
        }

        _suppressFilter = true;
        int previous = FilterColumn.SelectedIndex;
        FilterColumn.ItemsSource = new[] { "All columns" }.Concat(_headers.Select((h, i) => ColumnName(i))).ToList();
        FilterColumn.SelectedIndex = previous >= 0 && previous <= _headers.Count ? previous : 0;
        _suppressFilter = false;
    }

    /// <summary>
    /// Template column: a TextBlock whose background lights up when the cell matches the current search,
    /// and a TextBox for editing.
    /// </summary>
    private DataGridTemplateColumn MakeColumn(int i)
    {
        var path = $"Cells[{i}]";
        var highlight = _highlight.ForColumn(i);
        return new DataGridTemplateColumn
        {
            // A TextBlock header can be relabelled in place (sort arrows) without rebuilding columns.
            Header = new TextBlock { Text = HeaderLabel(i) },
            Width = new DataGridLength(EstimateWidth(i)),
            CanUserSort = true,
            SortMemberPath = path,
            ClipboardContentBinding = new Binding(path),
            CellTemplate = new FuncDataTemplate<Row>((_, _) =>
            {
                var text = new TextBlock
                {
                    Margin = new Thickness(12, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                };
                text.Bind(TextBlock.TextProperty, new Binding(path));
                var cell = new Border { Child = text };
                cell.Bind(Border.BackgroundProperty, new MultiBinding
                {
                    Bindings = { new Binding(path), new Binding(nameof(SearchHighlight.Version)) { Source = _highlight }, new Binding(".") },
                    Converter = highlight,
                });
                cell.Tag = i;
                return cell;
            }),
            CellEditingTemplate = new FuncDataTemplate<Row>((_, _) =>
            {
                var box = new TextBox { Tag = i, VerticalContentAlignment = VerticalAlignment.Center, AcceptsReturn = false };
                box.Bind(TextBox.TextProperty, new Binding(path) { Mode = BindingMode.TwoWay });
                return box;
            }),
        };
    }

    private string ColumnName(int i) => string.IsNullOrWhiteSpace(_headers[i]) ? $"Column {i + 1}" : _headers[i];

    private string HeaderLabel(int i)
    {
        var name = ColumnName(i);
        return i == _sortColumn ? (_sortAscending ? "↑ " : "↓ ") + name : name;
    }

    private void RefreshHeaders()
    {
        for (int i = 0; i < Table.Columns.Count && i < _headers.Count; i++)
            if (Table.Columns[i].Header is TextBlock label) label.Text = HeaderLabel(i);
    }

    private double EstimateWidth(int col)
    {
        int chars = ColumnName(col).Length + 3;
        foreach (var r in _rows.Take(200))
            if (col < r.Cells.Count) chars = Math.Max(chars, Math.Min(r.Cells[col].Length, 60));
        // Extra room for header padding and the sort arrow.
        return Math.Clamp(chars * 8 + 40, 80, 420);
    }

    // ───────────────────────────── documents ─────────────────────────────

    private bool IsPristine => _path == null && _loadingPath == null && !_dirty && _rows.All(r => r.Cells.All(string.IsNullOrEmpty));

    public static void OpenInBestWindow(string path)
    {
        path = Path.GetFullPath(path);
        var existing = OpenWindows.FirstOrDefault(w => string.Equals(w._path, path, StringComparison.Ordinal) ||
            string.Equals(w._loadingPath, path, StringComparison.Ordinal));
        if (existing != null)
        {
            ActivateDocument(existing);
            return;
        }
        var target = OpenWindows.FirstOrDefault(w => w.IsPristine);
        if (target == null)
        {
            target = new MainWindow();
            target.Show();
        }
        ActivateDocument(target);
        _ = target.LoadFileAsync(path);
    }

    private void NewDocument(int columns, int rows)
    {
        _headers = Enumerable.Range(1, columns).Select(i => $"Column {i}").ToList();
        _rows = Enumerable.Range(0, rows).Select(_ => new Row(Enumerable.Repeat("", columns))).ToList();
        _hasHeader = true;
        _headerToggle.IsChecked = true;
        _sortColumn = -1;
        ResetDuplicateFilter();
        RebuildColumns();
        ApplyFilter();
    }

    public async Task LoadFileAsync(string path, char? delimiter = null)
    {
        // Reserve the window before the first await so a batch cannot reuse it for another file.
        int generation = ++_loadGeneration;
        _loadingPath = Path.GetFullPath(path);
        UpdateTitle();
        try
        {
            await LoadFileCoreAsync(_loadingPath, delimiter, generation);
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                _loadingPath = null;
                if (!_isClosed) UpdateTitle();
            }
        }
    }

    private async Task LoadFileCoreAsync(string path, char? delimiter, int generation)
    {
        StatusText.Text = $"Loading {Path.GetFileName(path)}…";
        LoadedFile loaded;
        try
        {
            loaded = await Task.Run(() => Csv.Load(path, delimiter));
        }
        catch (Exception ex)
        {
            if (_isClosed || generation != _loadGeneration) return;
            StatusText.Text = "";
            await Dialogs.Info(this, "Could not open file", $"{path}\n\n{ex.Message}");
            return;
        }

        if (_isClosed || generation != _loadGeneration) return;

        int width = loaded.Records.Count == 0 ? 1 : loaded.Records.Max(r => r.Length);
        var records = loaded.Records;

        if (records.Count > 0 && loaded.HasHeader)
        {
            _headers = records[0].Concat(Enumerable.Repeat("", width - records[0].Length)).ToList();
            _rows = records.Skip(1).Select(r => MakeRow(r, width)).ToList();
        }
        else
        {
            // No header row in the file: every record is data and columns get generated names.
            _headers = Enumerable.Range(1, width).Select(i => $"Column {i}").ToList();
            _rows = records.Select(r => MakeRow(r, width)).ToList();
        }
        for (int i = 0; i < _rows.Count; i++) _rows[i].Order = i;

        _path = path;
        _delimiter = loaded.Delimiter;
        _encoding = loaded.Encoding;
        _newLine = loaded.NewLine;
        _hasHeader = loaded.HasHeader;
        _headerToggle.IsChecked = _hasHeader;
        _sortColumn = -1;
        _undo.Clear();
        _redo.Clear();
        UpdateUndoMenu();

        ResetDuplicateFilter();
        RebuildColumns();
        ApplyFilter();
        _dirty = false;
        UpdateTitle();
    }

    /// <summary>Re-reads the current file with a specific delimiter (null = auto-detect).</summary>
    private async Task ReopenAsync(char? delimiter)
    {
        if (_path == null) return;
        if (_dirty && await Dialogs.Ask(this, "Reopen file", "Reopening discards your unsaved changes.", "Cancel", "Reopen") != "Reopen")
            return;
        await LoadFileAsync(_path, delimiter);
    }

    private static Row MakeRow(string[] values, int width)
    {
        var row = new Row(values);
        row.EnsureWidth(width);
        return row;
    }

    private async Task OpenAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Delimited Text")
                {
                    Patterns = new[] { "*.csv", "*.tsv", "*.tab", "*.txt", "*.psv" },
                    AppleUniformTypeIdentifiers = new[] { "public.comma-separated-values-text", "public.tab-separated-values-text", "public.plain-text" },
                    MimeTypes = new[] { "text/csv", "text/tab-separated-values", "text/plain" },
                },
                FilePickerFileTypes.All,
            },
        });
        foreach (var f in files)
            if (f.TryGetLocalPath() is { } p)
                OpenInBestWindow(p);
    }

    private IEnumerable<IReadOnlyList<string>> RecordsForSave()
    {
        if (_hasHeader) yield return _headers;
        foreach (var r in _rows) yield return r.Cells;
    }

    private async Task<bool> SaveAsync(bool saveAs)
    {
        Table.CommitEdit();
        var path = _path;
        if (saveAs || path == null)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save As",
                SuggestedFileName = path != null ? Path.GetFileName(path) : "Untitled.csv",
                DefaultExtension = _delimiter == '\t' ? "tsv" : "csv",
                ShowOverwritePrompt = true,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("CSV (comma-separated)") { Patterns = new[] { "*.csv" }, AppleUniformTypeIdentifiers = new[] { "public.comma-separated-values-text" } },
                    new FilePickerFileType("TSV (tab-separated)") { Patterns = new[] { "*.tsv" }, AppleUniformTypeIdentifiers = new[] { "public.tab-separated-values-text" } },
                    new FilePickerFileType("Text") { Patterns = new[] { "*.txt" }, AppleUniformTypeIdentifiers = new[] { "public.plain-text" } },
                },
            });
            path = file?.TryGetLocalPath();
            if (path == null) return false;

            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".tsv" || ext == ".tab") _delimiter = '\t';
            else if (ext == ".csv" && _delimiter == '\t') _delimiter = ',';
        }

        try
        {
            var text = Csv.Write(RecordsForSave(), _delimiter, _newLine);
            await File.WriteAllTextAsync(path, text, _encoding);
        }
        catch (Exception ex)
        {
            await Dialogs.Info(this, "Could not save", $"{path}\n\n{ex.Message}");
            return false;
        }

        _path = path;
        _dirty = false;
        UpdateTitle();
        UpdateStatus();
        return true;
    }

    private async Task ExportAsync(ExportFormat format)
    {
        Table.CommitEdit();
        var baseName = _path != null ? Path.GetFileNameWithoutExtension(_path) : "Untitled";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export " + format.Name,
            SuggestedFileName = baseName + "." + format.Extension,
            DefaultExtension = format.Extension,
            ShowOverwritePrompt = true,
            FileTypeChoices = new[] { new FilePickerFileType(format.Name) { Patterns = new[] { "*." + format.Extension } } },
        });
        if (file?.TryGetLocalPath() is not { } path) return;

        var headers = _headers.Select((_, i) => ColumnName(i)).ToList();
        var rows = _visible.Select(r => r.ToArray()).ToList();
        try
        {
            var text = await Task.Run(() => format.Render(headers, rows, baseName));
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
            StatusText.Text = $"Exported {rows.Count:N0} rows to {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            await Dialogs.Info(this, "Export failed", ex.Message);
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_forceClose || !_dirty) return;
        e.Cancel = true;
        var name = _path != null ? Path.GetFileName(_path) : "Untitled";
        var answer = await Dialogs.Ask(this, "Unsaved changes", $"Do you want to save the changes made to “{name}”?", "Don't Save", "Cancel", "Save");
        if (answer == "Save" && !await SaveAsync(false)) return;
        if (answer is "Save" or "Don't Save")
        {
            _forceClose = true;
            _findWindow?.Close();
            Close();
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        foreach (var item in e.Data.GetFiles() ?? Enumerable.Empty<IStorageItem>())
            if (item.TryGetLocalPath() is { } p && File.Exists(p))
                OpenInBestWindow(p);
    }

    // ───────────────────────────── status ─────────────────────────────

    private void MarkDirty()
    {
        if (_dirty) return;
        _dirty = true;
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        var path = _loadingPath ?? _path;
        var name = path != null ? Path.GetFileName(path) : "Untitled";
        Title = _dirty ? $"{name} — Edited" : name;
        RefreshWindowMenus();
    }

    private void UpdateStatus()
    {
        var shown = _visible.Count == _rows.Count ? $"{_rows.Count:N0} rows" : $"{_visible.Count:N0} of {_rows.Count:N0} rows";
        var header = _hasHeader ? "header row" : "no header (names added)";
        StatusText.Text = $"{shown} · {_headers.Count} columns · {Csv.DelimiterName(_delimiter)} · {header} · {_encoding.WebName}";
        EmptyHint.IsVisible = _path == null && _rows.Count <= 1 && _rows.All(r => r.Cells.All(string.IsNullOrEmpty));
        UpdateCellStatus();
    }

    private void UpdateCellStatus()
    {
        if (_cells.HasSelection)
        {
            StatusRight.Text = _cells.Count == 1
                ? $"Row {_cells.Row + 1:N0} · {ColumnName(_cells.Column)}"
                : $"{_cells.Count:N0} cells · rows {_cells.Top + 1:N0}–{_cells.Bottom + 1:N0} · {ColumnName(_cells.Left)}–{ColumnName(_cells.Right)}";
            return;
        }
        int col = CurrentCol;
        int row = Table.SelectedItem is Row r ? _visible.IndexOf(r) : -1;
        int selected = Table.SelectedItems?.Count ?? 0;
        var parts = new List<string>();
        if (row >= 0) parts.Add($"Row {row + 1:N0}");
        if (col >= 0 && col < _headers.Count) parts.Add(ColumnName(col));
        if (selected > 1) parts.Add($"{selected:N0} selected");
        StatusRight.Text = string.Join(" · ", parts);
    }

    // ───────────────────────────── undo ─────────────────────────────

    private void Push(UndoEntry entry)
    {
        _undo.Add(entry);
        // Drop the oldest history when it grows too long or holds too many snapshot cells.
        while (_undo.Count > 1 && (_undo.Count > MaxUndo || _undo.Sum(u => u.Cost) > MaxUndoCells))
            _undo.RemoveAt(0);
        _redo.Clear();
        MarkDirty();
        UpdateUndoMenu();
    }

    private void Undo()
    {
        Table.CommitEdit();
        if (_undo.Count == 0) return;
        var e = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        e.Undo();
        _redo.Add(e);
        MarkDirty();
        UpdateUndoMenu();
        UpdateStatus();
    }

    private void Redo()
    {
        Table.CommitEdit();
        if (_redo.Count == 0) return;
        var e = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        e.Redo();
        _undo.Add(e);
        MarkDirty();
        UpdateUndoMenu();
        UpdateStatus();
    }

    private void UpdateUndoMenu()
    {
        _undoItem.Header = _undo.Count > 0 ? $"Undo {_undo[^1].Name}" : "Undo";
        _undoItem.IsEnabled = _undo.Count > 0;
        _redoItem.Header = _redo.Count > 0 ? $"Redo {_redo[^1].Name}" : "Redo";
        _redoItem.IsEnabled = _redo.Count > 0;
    }

    private TableSnapshot Snapshot() =>
        new(_headers.ToArray(), _rows.ToArray(), _rows.Select(r => r.ToArray()).ToArray(), _hasHeader);

    private void Restore(TableSnapshot s)
    {
        _headers = s.Headers.ToList();
        _rows = s.Rows.ToList();
        for (int i = 0; i < s.Rows.Length; i++) s.Rows[i].SetAll(s.Values[i]);
        _hasHeader = s.HasHeader;
        _headerToggle.IsChecked = _hasHeader;
        if (_sortColumn >= _headers.Count) _sortColumn = -1;
        ResetDuplicateFilter();
        RebuildColumns();
        ApplyFilter();
    }

    /// <summary>Runs an operation that can touch any part of the table, with snapshot-based undo.</summary>
    private void Structural(string name, Func<bool> operation)
    {
        Table.CommitEdit();
        var before = Snapshot();
        if (!operation()) return;
        var after = Snapshot();
        ResetDuplicateFilter();
        RebuildColumns();
        ApplyFilter();
        long cost = (long)(before.Values.Length + after.Values.Length) * Math.Max(1, _headers.Count);
        Push(new UndoEntry(name, () => Restore(before), () => Restore(after), cost));
    }

    // ───────────────────────────── filter & sort ─────────────────────────────

    /// <summary>
    /// Rebuilds the visible rows from the search box. Small tables filter inline; large ones filter on
    /// background threads, and a newer keystroke cancels an older search still running.
    /// </summary>
    private void ApplyFilter()
    {
        var search = CurrentSearch();
        _highlight.Search = search.IsEmpty ? null : search;
        _filterCts?.Cancel();
        int generation = ++_filterGeneration;

        if (search.IsEmpty || _rows.Count < 20_000)
        {
            ShowRows(search.IsEmpty ? _rows.ToList() : _rows.Where(search.Matches).ToList(), !search.IsEmpty);
            return;
        }

        var cts = _filterCts = new CancellationTokenSource();
        var rows = _rows.ToArray();
        MatchCount.Text = "Searching…";
        Task.Run(() => search.Filter(rows, cts.Token), cts.Token).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && generation == _filterGeneration)
                ShowRows(t.Result, true);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ShowRows(List<Row> rows, bool searching)
    {
        rows = FilterDuplicateRows(rows);
        searching |= DuplicateFilterActive;
        ClearCellSelection();
        var keep = Table.SelectedItem as Row;
        _visible = new ObservableCollection<Row>(rows);
        ReindexVisibleRows();
        Table.ItemsSource = _visible;
        if (keep != null && _visible.Contains(keep))
        {
            Table.SelectedItem = keep;
            Table.ScrollIntoView(keep, Table.CurrentColumn);
        }
        _appliedQuery = FilterBox.Text ?? "";
        MatchCount.Text = searching ? (rows.Count == 1 ? "1 row" : $"{rows.Count:N0} rows") : "";
        NoMatches.IsVisible = searching && rows.Count == 0;
        UpdateStatus();
    }

    private RowSearch CurrentSearch() => new(
        FilterBox.Text ?? "",
        FilterColumn.SelectedIndex - 1,
        FilterCase.IsChecked == true,
        matchAll: FilterMode.SelectedIndex != 1,
        invert: FilterInvert.IsChecked == true);

    private string _appliedQuery = "";

    private bool CurrentQueryChanged() => (FilterBox.Text ?? "") != _appliedQuery;

    /// <summary>Moves the cell cursor to the next/previous highlighted cell, wrapping around the visible rows.</summary>
    private void JumpToMatch(int direction)
    {
        var search = _highlight.Search;
        int cols = _headers.Count;
        if (search == null || search.Invert || _visible.Count == 0 || cols == 0)
        {
            if (search == null) FocusSearch();
            return;
        }

        int startRow = Table.SelectedItem is Row sr ? Math.Max(0, _visible.IndexOf(sr)) : (direction > 0 ? 0 : _visible.Count - 1);
        int startCol = CurrentCol >= 0 ? CurrentCol : (direction > 0 ? -1 : cols);
        long total = (long)_visible.Count * cols;
        long start = (long)startRow * cols + startCol;

        for (long step = 1; step <= total; step++)
        {
            long p = ((start + direction * step) % total + total) % total;
            int r = (int)(p / cols), c = (int)(p % cols);
            var row = _visible[r];
            if (c < row.Cells.Count && search.IsCellMatch(row.Cells[c], c))
            {
                SelectCell(r, c);
                Table.ScrollIntoView(row, Table.Columns[c]);
                StatusRight.Text = $"Match in row {r + 1:N0}, {ColumnName(c)}";
                return;
            }
        }
        StatusRight.Text = "No matching cells";
    }

    private void FocusSearch()
    {
        FilterBox.Focus();
        FilterBox.SelectAll();
    }

    private void ClearFilter()
    {
        ResetDuplicateFilter();
        _suppressFilter = true;
        FilterBox.Text = "";
        FilterColumn.SelectedIndex = 0;
        FilterInvert.IsChecked = false;
        _suppressFilter = false;
        _filterTimer.Stop();
        ApplyFilter();
    }

    private bool _sorting;

    /// <summary>Reorders all rows (the saved file follows this order) with undo; runs off the UI thread.</summary>
    private async Task ReorderAsync(string label, int col, bool ascending, Func<List<Row>, List<Row>> reorder)
    {
        if (_sorting) return;
        Table.CommitEdit();
        _sorting = true;
        Table.IsEnabled = false;
        var oldStatus = StatusText.Text;
        StatusText.Text = $"{label}…";
        try
        {
            var before = _rows.ToList();
            var (prevCol, prevAsc) = (_sortColumn, _sortAscending);
            var after = await Task.Run(() => reorder(before));

            void Apply(List<Row> order, int sortCol, bool asc)
            {
                _rows = order.ToList();
                (_sortColumn, _sortAscending) = (sortCol, asc);
                RefreshHeaders();
                ApplyFilter();
            }

            Apply(after, col, ascending);
            Push(new UndoEntry(label, () => Apply(before, prevCol, prevAsc), () => Apply(after, col, ascending)));
        }
        catch (Exception ex)
        {
            StatusText.Text = oldStatus;
            await Dialogs.Info(this, "Sort failed", ex.Message);
        }
        finally
        {
            Table.IsEnabled = true;
            _sorting = false;
        }
    }

    private Task SortByAsync(int col, bool ascending)
    {
        if (col < 0 || col >= _headers.Count)
        {
            StatusText.Text = "Click a cell in the column you want to sort, or click its header.";
            return Task.CompletedTask;
        }
        return ReorderAsync($"Sort by {ColumnName(col)}", col, ascending, rows => Sorter.Sort(rows, col, ascending));
    }

    private Task ClearSortAsync() =>
        ReorderAsync("Restore Original Order", -1, true, rows => rows.OrderBy(r => r.Order).ToList());

    // ───────────────────────────── rows ─────────────────────────────

    private List<Row> SelectedRows() => _cells.HasSelection
        ? _visible.Skip(_cells.Top).Take(_cells.Bottom - _cells.Top + 1).ToList()
        : (Table.SelectedItems?.OfType<Row>() ?? Enumerable.Empty<Row>()).ToList();

    /// <summary>Inserts rows after the current selection (or at the end) with lightweight undo.</summary>
    private void InsertRows(string name, List<Row> newRows)
    {
        if (newRows.Count == 0) return;
        Table.CommitEdit();
        int anchor = Table.SelectedItem is Row sel ? _rows.IndexOf(sel) + 1 : _rows.Count;
        int visibleAnchor = Table.SelectedItem is Row vs ? _visible.IndexOf(vs) + 1 : _visible.Count;

        // Slot new rows right after their anchor in "original order".
        double baseOrder = anchor > 0 ? _rows[anchor - 1].Order : (_rows.Count > 0 ? _rows.Min(r => r.Order) - 1 : 0);
        for (int i = 0; i < newRows.Count; i++) newRows[i].Order = baseOrder + (i + 1) / (newRows.Count + 1.0) * 1e-3;
        _rows.InsertRange(anchor, newRows);
        for (int i = 0; i < newRows.Count; i++) _visible.Insert(visibleAnchor + i, newRows[i]);
        ClearCellSelection();
        ReindexVisibleRows();
        SelectCell(visibleAnchor, 0);
        Table.ScrollIntoView(newRows[0], Table.Columns.FirstOrDefault());

        Push(new UndoEntry(name,
            () => { _rows.RemoveRange(anchor, newRows.Count); ApplyFilter(); },
            () => { _rows.InsertRange(anchor, newRows); ApplyFilter(); }));
        UpdateStatus();
    }

    private void InsertRow() =>
        InsertRows("Insert Row", new List<Row> { new(Enumerable.Repeat("", _headers.Count)) });

    private void DuplicateRows()
    {
        var selected = SelectedRows().OrderBy(r => _rows.IndexOf(r)).ToList();
        if (selected.Count == 0) return;
        Table.SelectedItem = selected[^1];
        InsertRows("Duplicate Rows", selected.Select(r => new Row(r.Cells)).ToList());
    }

    private void DeleteRows()
    {
        if (!Table.CommitEdit()) return;
        var selected = SelectedRows();
        if (selected.Count == 0) { StatusText.Text = "Select cells or row numbers, then Delete Rows."; return; }
        int selectedColumn = Math.Max(0, CurrentCol);
        ClearCellSelection();

        var set = selected.ToHashSet();
        var removed = new List<(int Index, Row Row)>();
        for (int i = 0; i < _rows.Count; i++)
            if (set.Contains(_rows[i])) removed.Add((i, _rows[i]));

        int firstVisible = _visible.IndexOf(selected[0]);
        _rows.RemoveAll(set.Contains);
        foreach (var r in selected) _visible.Remove(r);
        if (DuplicateFilterActive) ApplyFilter();
        ReindexVisibleRows();
        if (_visible.Count > 0)
            SelectCell(Math.Clamp(firstVisible, 0, _visible.Count - 1), selectedColumn);

        Push(new UndoEntry(selected.Count == 1 ? "Delete Row" : "Delete Rows",
            () =>
            {
                foreach (var (index, row) in removed) _rows.Insert(index, row);
                ApplyFilter();
            },
            () =>
            {
                _rows.RemoveAll(set.Contains);
                ApplyFilter();
            }));
        UpdateStatus();
    }

    private async Task PasteRowsAsync()
    {
        var text = Clipboard is { } cb ? await cb.GetTextAsync() : null;
        if (string.IsNullOrEmpty(text)) return;
        var delimiter = text.Contains('\t') ? '\t' : Csv.DetectDelimiter(text);
        var records = Csv.Parse(text, delimiter);
        if (records.Count == 0) return;

        int width = records.Max(r => r.Length);
        if (width > _headers.Count)
        {
            // Grow the table first so the pasted values have somewhere to go.
            Structural("Add Columns", () =>
            {
                for (int i = _headers.Count; i < width; i++) _headers.Add($"Column {i + 1}");
                foreach (var r in _rows) r.EnsureWidth(_headers.Count);
                return true;
            });
        }
        InsertRows("Paste Rows", records.Select(r => MakeRow(r, _headers.Count)).ToList());
    }

    // ───────────────────────────── columns ─────────────────────────────

    private async Task InsertColumnAsync()
    {
        var name = await Dialogs.Prompt(this, "Insert Column", "Name of the new column:", $"Column {_headers.Count + 1}");
        if (name == null) return;
        int at = CurrentCol >= 0 ? CurrentCol + 1 : _headers.Count;
        Structural("Insert Column", () =>
        {
            _headers.Insert(at, name);
            foreach (var r in _rows)
            {
                r.EnsureWidth(at);
                r.Cells.Insert(at, "");
            }
            if (_sortColumn >= at) _sortColumn++;
            return true;
        });
    }

    private async Task RenameColumnAsync()
    {
        int col = CurrentCol;
        if (col < 0) { StatusText.Text = "Select a cell in the column to rename."; return; }
        var name = await Dialogs.Prompt(this, "Rename Column", "Column name:", _headers[col]);
        if (name == null || name == _headers[col]) return;
        Structural("Rename Column", () =>
        {
            _headers[col] = name;
            return true;
        });
    }

    private void DeleteColumn()
    {
        int col = CurrentCol;
        if (col < 0) { StatusText.Text = "Select a cell in the column to delete."; return; }
        if (_headers.Count == 1) { StatusText.Text = "A table needs at least one column."; return; }
        Structural("Delete Column", () =>
        {
            _headers.RemoveAt(col);
            foreach (var r in _rows)
                if (col < r.Cells.Count) r.Cells.RemoveAt(col);
            if (_sortColumn == col) _sortColumn = -1;
            else if (_sortColumn > col) _sortColumn--;
            return true;
        });
    }

    private void MoveColumn(int delta)
    {
        int col = CurrentCol;
        int target = col + delta;
        if (col < 0 || target < 0 || target >= _headers.Count) return;
        Structural("Move Column", () =>
        {
            (_headers[col], _headers[target]) = (_headers[target], _headers[col]);
            foreach (var r in _rows)
                (r.Cells[col], r.Cells[target]) = (r.Cells[target], r.Cells[col]);
            if (_sortColumn == col) _sortColumn = target;
            else if (_sortColumn == target) _sortColumn = col;
            return true;
        });
        Table.CurrentColumn = Table.Columns[target];
    }

    private void ToggleHeader()
    {
        bool turnOn = !_hasHeader;
        Structural(turnOn ? "Use First Row as Header" : "Header to Row", () =>
        {
            if (turnOn)
            {
                if (_rows.Count == 0) return false;
                _headers = _rows[0].ToArray().ToList();
                _rows.RemoveAt(0);
            }
            else
            {
                _rows.Insert(0, new Row(_headers) { Order = _rows.Count > 0 ? _rows.Min(r => r.Order) - 1 : 0 });
                _headers = Enumerable.Range(1, _headers.Count).Select(i => $"Column {i}").ToList();
            }
            _hasHeader = turnOn;
            _sortColumn = -1;
            return true;
        });
        _headerToggle.IsChecked = _hasHeader;
    }

    // ───────────────────────────── auto-fill ─────────────────────────────

    private async Task AutoFillAsync()
    {
        Table.CommitEdit();
        var names = _headers.Select((_, i) => ColumnName(i)).ToList();
        var request = await new AutoFillWindow(names, CurrentCol, SelectedRows().Count > 1).ShowDialog<FillRequest?>(this);
        if (request != null) ApplyFill(request);
    }

    /// <summary>Fills (and optionally creates) a column from a pattern in one undoable step.</summary>
    public void ApplyFill(FillRequest request)
    {
        var selected = SelectedRows().ToHashSet();
        var visible = _visible.ToHashSet();
        int filled = 0, added = 0;

        Structural("Auto-Fill", () =>
        {
            int col = request.Column;
            if (col < 0)
            {
                _headers.Add(request.NewColumnName);
                foreach (var r in _rows) r.EnsureWidth(_headers.Count);
                col = _headers.Count - 1;
            }

            var newRows = new HashSet<Row>();
            double order = _rows.Count > 0 ? _rows.Max(r => r.Order) : 0;
            for (int i = 0; i < request.AddRows; i++)
            {
                var row = new Row(Enumerable.Repeat("", _headers.Count)) { Order = ++order };
                _rows.Add(row);
                newRows.Add(row);
            }
            added = newRows.Count;

            // %[Column]% lookups use the names shown in the header row; the first match wins.
            var lookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _headers.Count; i++) lookup.TryAdd(ColumnName(i), i);

            var rng = new Random();
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                row.EnsureWidth(col + 1);
                bool target = newRows.Contains(row) || request.Scope switch
                {
                    FillScope.EmptyCells => string.IsNullOrWhiteSpace(row.Cells[col]),
                    FillScope.SelectedRows => selected.Contains(row),
                    FillScope.VisibleRows => visible.Contains(row),
                    _ => true,
                };
                if (!target) continue;
                row.Cells[col] = request.Pattern.Generate(rng, i + 1,
                    name => lookup.TryGetValue(name, out var c) && c < row.Cells.Count ? row.Cells[c] : null);
                filled++;
            }
            return filled > 0 || request.Column < 0;
        });
        StatusText.Text = $"Auto-filled {filled:N0} cells" + (added > 0 ? $", added {added:N0} rows." : ".");
    }

    // ───────────────────────────── data cleaning ─────────────────────────────

    private void TrimWhitespace()
    {
        int changed = 0;
        Structural("Trim Whitespace", () =>
        {
            for (int i = 0; i < _headers.Count; i++)
                _headers[i] = _headers[i].Trim();
            foreach (var r in _rows)
                for (int c = 0; c < r.Cells.Count; c++)
                {
                    var t = r.Cells[c].Trim();
                    if (t != r.Cells[c]) { r.Cells[c] = t; changed++; }
                }
            return changed > 0;
        });
        StatusText.Text = $"Trimmed {changed:N0} cells.";
    }

    private void RemoveEmptyRows()
    {
        int removed = 0;
        Structural("Remove Empty Rows", () =>
        {
            removed = _rows.RemoveAll(r => r.Cells.All(string.IsNullOrWhiteSpace));
            return removed > 0;
        });
        StatusText.Text = $"Removed {removed:N0} empty rows.";
    }

    private void RemoveDuplicateRows()
    {
        int removed = 0;
        Structural("Remove Duplicates", () =>
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            removed = _rows.RemoveAll(r => !seen.Add(string.Join("\u001F", r.Cells)));
            return removed > 0;
        });
        StatusText.Text = $"Removed {removed:N0} duplicate rows.";
    }

    private void FillDown()
    {
        int col = CurrentCol;
        if (col < 0) { StatusText.Text = "Select a cell in the column to fill."; return; }
        int filled = 0;
        Structural("Fill Down", () =>
        {
            string last = "";
            foreach (var r in _visible)
            {
                if (string.IsNullOrWhiteSpace(r.Cells[col])) { if (last.Length > 0) { r.Cells[col] = last; filled++; } }
                else last = r.Cells[col];
            }
            return filled > 0;
        });
        StatusText.Text = $"Filled {filled:N0} cells.";
    }

    private void ChangeCase(string name, Func<string, string> transform)
    {
        int col = CurrentCol;
        if (col < 0) { StatusText.Text = "Select a cell in the column to change."; return; }
        Structural(name, () =>
        {
            bool any = false;
            foreach (var r in _visible)
            {
                var v = transform(r.Cells[col]);
                if (v != r.Cells[col]) { r.Cells[col] = v; any = true; }
            }
            return any;
        });
    }

    // ───────────────────────────── statistics ─────────────────────────────

    private void ShowStatistics()
    {
        int col = CurrentCol;
        if (col < 0) { StatusText.Text = "Select a cell in the column to analyse."; return; }
        var values = _visible.Select(r => r.Cells[col]).ToList();
        var name = ColumnName(col);

        var sb = new StringBuilder();
        int empty = values.Count(string.IsNullOrWhiteSpace);
        var groups = values.Where(v => !string.IsNullOrWhiteSpace(v))
            .GroupBy(v => v, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.CurrentCulture)
            .ToList();
        var numbers = values.Select(v => Values.TryNumber(v, out var d) ? (double?)d : null).Where(d => d.HasValue).Select(d => d!.Value).ToList();

        sb.AppendLine($"Column:          {name}");
        sb.AppendLine($"Rows (visible):  {values.Count:N0}");
        sb.AppendLine($"Empty:           {empty:N0}");
        sb.AppendLine($"Distinct values: {groups.Count:N0}");
        if (values.Count > 0)
        {
            sb.AppendLine($"Shortest text:   {values.Min(v => v.Length)} chars");
            sb.AppendLine($"Longest text:    {values.Max(v => v.Length)} chars");
        }

        if (numbers.Count > 0)
        {
            numbers.Sort();
            double sum = numbers.Sum();
            double mean = sum / numbers.Count;
            double median = numbers.Count % 2 == 1 ? numbers[numbers.Count / 2] : (numbers[numbers.Count / 2 - 1] + numbers[numbers.Count / 2]) / 2;
            double sd = numbers.Count > 1 ? Math.Sqrt(numbers.Sum(x => (x - mean) * (x - mean)) / (numbers.Count - 1)) : 0;
            sb.AppendLine();
            sb.AppendLine($"Numeric values:  {numbers.Count:N0}");
            sb.AppendLine($"Sum:             {sum:G15}");
            sb.AppendLine($"Mean:            {mean:G10}");
            sb.AppendLine($"Median:          {median:G10}");
            sb.AppendLine($"Std deviation:   {sd:G10}");
            sb.AppendLine($"Minimum:         {numbers[0]:G15}");
            sb.AppendLine($"Maximum:         {numbers[^1]:G15}");
        }

        sb.AppendLine();
        sb.AppendLine("Most frequent values:");
        foreach (var g in groups.Take(25))
        {
            var key = g.Key.Length > 48 ? g.Key[..45] + "…" : g.Key;
            sb.AppendLine($"  {g.Count(),8:N0}  {key}");
        }

        Dialogs.ShowText(this, $"Statistics — {name}", sb.ToString());
    }

    // ───────────────────────────── find & replace ─────────────────────────────

    private long _searchColumnRevision;

    internal SearchSelection? CaptureSearchSelection()
    {
        if (!Table.CommitEdit() || !_cells.HasSelection) return null;
        return new SearchSelection(_visible.Skip(_cells.Top).Take(_cells.Bottom - _cells.Top + 1),
            _cells.Left, _cells.Right, _searchColumnRevision);
    }

    private void ShowFind()
    {
        if (_findWindow == null)
        {
            _findWindow = new FindReplaceWindow(this);
            _findWindow.Closed += (_, _) => _findWindow = null;
            _findWindow.Show(this);
        }
        _findWindow.Activate();
    }

    private string? ValidateSearch(SearchOptions o, out System.Text.RegularExpressions.Regex? rx)
    {
        rx = null;
        if (!Table.CommitEdit()) return "Finish editing the current cell first.";
        if (o.Find.Length == 0) return "Enter text to find.";
        if (o.Scope == SearchScope.CurrentColumn && CurrentCol < 0) return "Select a cell in the column to search.";
        if (o.Scope == SearchScope.SelectedCells)
        {
            if (o.Selection == null) return "Select cells, then click Use current selection.";
            if (o.Selection.ColumnRevision != _searchColumnRevision)
                return "The table changed. Select cells again, then click Use current selection.";
        }
        rx = o.BuildRegex();
        return rx == null ? "Invalid regular expression." : null;
    }

    private IEnumerable<(Row Row, int VisibleRow, int Column)> SearchCells(SearchOptions o)
    {
        int left = o.Scope == SearchScope.CurrentColumn ? CurrentCol :
            o.Scope == SearchScope.SelectedCells ? o.Selection!.FirstColumn : 0;
        int right = o.Scope == SearchScope.CurrentColumn ? left :
            o.Scope == SearchScope.SelectedCells ? o.Selection!.LastColumn : _headers.Count - 1;
        for (int r = 0; r < _visible.Count; r++)
        {
            var row = _visible[r];
            if (o.Scope == SearchScope.SelectedCells && !o.Selection!.Rows.Contains(row)) continue;
            for (int c = left; c <= right; c++) yield return (row, r, c);
        }
    }

    private bool InSearchScope(SearchOptions o, Row row, int column) => o.Scope switch
    {
        SearchScope.CurrentColumn => column == CurrentCol,
        SearchScope.SelectedCells => o.Selection!.Rows.Contains(row) &&
            column >= o.Selection.FirstColumn && column <= o.Selection.LastColumn,
        _ => true,
    };

    public string FindNext(SearchOptions o)
    {
        if (ValidateSearch(o, out var rx) is { } error) return error;
        int cols = _headers.Count;
        if (_visible.Count == 0 || cols == 0) return "No rows to search.";

        int startRow = Table.SelectedItem is Row sr ? Math.Max(0, _visible.IndexOf(sr)) : 0;
        int startCol = Math.Max(CurrentCol, 0);
        long start = (long)startRow * cols + startCol;
        (Row Row, int VisibleRow, int Column)? wrap = null;
        foreach (var cell in SearchCells(o))
        {
            if (!rx!.IsMatch(cell.Row.Cells[cell.Column])) continue;
            if ((long)cell.VisibleRow * cols + cell.Column > start) return ShowMatch(cell);
            wrap ??= cell;
        }
        return wrap is { } match ? ShowMatch(match) : "Not found.";

        string ShowMatch((Row Row, int VisibleRow, int Column) cell)
        {
            SelectCell(cell.VisibleRow, cell.Column);
            Table.ScrollIntoView(cell.Row, Table.Columns[cell.Column]);
            return $"Found in row {cell.VisibleRow + 1:N0}, {ColumnName(cell.Column)}.";
        }
    }

    public string CountMatches(SearchOptions o)
    {
        if (ValidateSearch(o, out var rx) is { } error) return error;
        long cells = SearchCells(o).LongCount(cell => rx!.IsMatch(cell.Row.Cells[cell.Column]));
        return cells == 1 ? "1 matching cell." : $"{cells:N0} matching cells.";
    }

    private static string ReplacementText(SearchOptions o) => o.UseRegex ? o.Replace : o.Replace.Replace("$", "$$");

    public string ReplaceCurrent(SearchOptions o)
    {
        if (ValidateSearch(o, out var rx) is { } error) return error;
        int c = CurrentCol;
        if (Table.SelectedItem is Row row && c >= 0 &&
            _visibleIndices.ContainsKey(row) && InSearchScope(o, row, c) && rx!.IsMatch(row.Cells[c]))
        {
            var oldValue = row.Cells[c];
            var newValue = rx.Replace(oldValue, ReplacementText(o));
            if (newValue != oldValue) ApplyCellChanges("Replace", new() { (row, c, oldValue, newValue) });
        }
        return FindNext(o);
    }

    public string ReplaceAll(SearchOptions o)
    {
        if (ValidateSearch(o, out var rx) is { } error) return error;
        var replacement = ReplacementText(o);
        var changes = new List<(Row Row, int Col, string Old, string New)>();
        foreach (var (row, _, col) in SearchCells(o))
        {
            var value = rx!.Replace(row.Cells[col], replacement);
            if (value != row.Cells[col]) changes.Add((row, col, row.Cells[col], value));
        }
        ApplyCellChanges("Replace All", changes);
        return changes.Count == 1 ? "Replaced in 1 cell." : $"Replaced in {changes.Count:N0} cells.";
    }
}
