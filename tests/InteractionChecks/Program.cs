using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.VisualTree;
using TableEdit;
using TableEdit.Model;

// An integration smoke runner using native X11 mouse/keyboard input. No test-only
// selection methods are called: selection input passes through Avalonia's real hit testing,
// pointer capture, native DataGrid handlers, menus and toolbar controls. Paste logic
// is also checked directly when this display cannot roundtrip the OS clipboard.
internal static class Program
{
    private static MainWindow _window = null!;
    private static DataGrid _grid = null!;
    private static NativeInput _input = null!;
    private static int _passed;
    private static int _result;
    private static string _directory = "";
    private const string Fixture = "Name,Value,Group\nDelta,40,red\nAlpha,10,blue\nCharlie,30,red\nBravo,20,green\nEcho,50,blue\nFoxtrot,60,\n";

    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            Console.Error.WriteLine("Run these native-input checks on Linux with an X11 DISPLAY (see tests/README.md).");
            return 2;
        }
        AppBuilder.Configure<App>().UsePlatformDetect().AfterSetup(_ =>
            Dispatcher.UIThread.Post(async () =>
            {
                var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
                try
                {
                    _window = (MainWindow)desktop.MainWindow!;
                    _window.Show();
                    _window.Activate();
                    _grid = _window.FindControl<DataGrid>("Table")!;
                    _input = new NativeInput();
                    _directory = Path.Combine(Path.GetTempPath(), "tableedit-interaction-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(_directory);
                    await Pause();
                    if (args.Contains("--scope-only"))
                    {
                        await CheckScopedFindReplace();
                        await CheckSelectedTransforms();
                    }
                    else await RunChecks();
                    Console.WriteLine($"PASS: {_passed} interaction checks.");
                }
                catch (Exception error)
                {
                    _result = 1;
                    Console.Error.WriteLine(error);
                }
                finally
                {
                    _input?.Dispose();
                    if (!string.IsNullOrEmpty(_directory)) Directory.Delete(_directory, recursive: true);
                    // This process owns the test document; skip the unsaved-change prompt.
                    typeof(MainWindow).GetField("_forceClose", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(_window, true);
                    desktop.Shutdown(_result);
                }
            })).StartWithClassicDesktopLifetime(Array.Empty<string>());
        return _result;
    }

    private static async Task RunChecks()
    {
        await LoadFixture();
        await ClickCell(1, 1);
        Equal("10", SelectedText(), "single click selects one cell, not the row");

        await ClickCell(4, 1, shift: true);
        Equal(Lines("10", "30", "20", "50"), SelectedText(), "Shift-click selects a column interval from the anchor");
        await ClickCell(2, 2, shift: true);
        Equal(Lines("10\tblue", "30\tred"), SelectedText(), "Shift-click preserves anchor and resizes the rectangle");

        await Drag(Cell(0, 0), Cell(2, 1));
        Equal(Lines("Delta\t40", "Alpha\t10", "Charlie\t30"), SelectedText(), "mouse drag selects a rectangle across rows and columns");
        await Drag(Cell(4, 2), Cell(2, 1));
        Equal(Lines("30\tred", "20\tgreen", "50\tblue"), SelectedText(), "reverse drag normalizes rectangle bounds");

        await ClickCell(1, 1);
        await Key("Down", control: true, shift: true);
        Equal(Lines("10", "30", "20", "50", "60"), SelectedText(), "Ctrl+Shift+Down extends only the current column");
        await Key("Up", control: true, shift: true);
        Equal(Lines("40", "10"), SelectedText(), "Ctrl+Shift+Up preserves the range anchor");

        await ClickCell(2, 1);
        await Key("space", control: true);
        Equal(Lines("40", "10", "30", "20", "50", "60"), SelectedText(), "Ctrl+Space selects the whole visible column");
        await Key("space", shift: true);
        Equal("Charlie\t30\tred", SelectedText(), "Shift+Space selects the active row");

        await ClickCell(1, 1);
        await ClickCell(2, 2, shift: true);
        await Key("Delete");
        var rows = Rows();
        True(rows.Count == 6 && rows[1].Cells[0] == "Alpha" && rows[1].Cells[1] == "" && rows[1].Cells[2] == "" &&
             rows[2].Cells[1] == "" && rows[2].Cells[2] == "" && rows[3].Cells[1] == "20",
            "Delete clears selected cells without deleting rows or neighboring values");
        await Key("z", control: true);
        Equal("10\tblue", string.Join('\t', Rows()[1].Cells.Skip(1)), "Undo restores cleared cells");

        await ClickCell(1, 1);
        await ClickCell(2, 2, shift: true);
        await Click(_window.FindControl<Button>("DeleteRowsButton")!);
        Equal(Lines("Delta", "Bravo", "Echo", "Foxtrot"), Lines(Rows().Select(r => r.Cells[0]).ToArray()),
            "Delete Rows removes the rows intersecting the selected cells");
        await Key("z", control: true);
        True(Rows().Count == 6 && Rows()[1].Cells[0] == "Alpha" && Rows()[2].Cells[0] == "Charlie",
            "Undo restores deleted rows at their original positions");

        await ClickHeader(0);
        await WaitUntil(() => Rows()[0].Cells[0] == "Alpha");
        Equal(Lines("Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot"), Lines(Rows().Select(r => r.Cells[0]).ToArray()),
            "first header click sorts A to Z");
        await ClickHeader(0);
        await WaitUntil(() => Rows()[0].Cells[0] == "Foxtrot");
        Equal(Lines("Foxtrot", "Echo", "Delta", "Charlie", "Bravo", "Alpha"), Lines(Rows().Select(r => r.Cells[0]).ToArray()),
            "second header click sorts Z to A");

        await LoadFixture();
        await ClickCell(1, 2);
        await Click(_window.FindControl<Button>("FindDuplicatesButton")!);
        Equal(Lines("Delta", "Alpha", "Charlie", "Echo"), Lines(Rows().Select(r => r.Cells[0]).ToArray()),
            "Find Duplicates shows repeated current-column values even when complete rows differ");
        True(_window.FindControl<TextBlock>("StatusText")!.Text!.Contains("2"), "duplicate result describes duplicate groups");
        await ClickCell(0, 2);
        await Click(_window.FindControl<Button>("DeleteRowsButton")!);
        Equal(Lines("Alpha", "Echo"), Lines(Rows().Select(r => r.Cells[0]).ToArray()),
            "deleting a duplicate hides its now-unique partner without deleting that partner");
        await Key("z", control: true);
        True(Rows().Count == 4, "Undo restores duplicate groups after row deletion");
        await Click(_window.FindControl<Button>("ShowAllButton")!);
        True(Rows().Count == 6, "Show All restores every row after duplicate filtering");
        await ClickCell(0, 0);
        await Click(_window.FindControl<Button>("FindDuplicatesButton")!);
        True(Rows().Count == 6 && _window.FindControl<TextBlock>("StatusText")!.Text!.Contains("No duplicate", StringComparison.OrdinalIgnoreCase),
            "unique column reports no duplicates and leaves rows visible");

        await LoadFixture();
        var headerRow = Cell(1, 0).GetVisualAncestors().OfType<DataGridRow>().First();
        await Click(headerRow.GetVisualDescendants().OfType<DataGridRowHeader>().First());
        Equal("Alpha\t10\tblue", SelectedText(), "row-number click selects all cells in that row");
        var laterRow = Cell(3, 0).GetVisualAncestors().OfType<DataGridRow>().First();
        await Click(laterRow.GetVisualDescendants().OfType<DataGridRowHeader>().First(), shift: true);
        Equal(Lines("Alpha\t10\tblue", "Charlie\t30\tred", "Bravo\t20\tgreen"), SelectedText(),
            "Shift-click on row numbers selects a row interval");
        await Key("Delete", control: true);
        Equal(Lines("Delta", "Echo", "Foxtrot"), Lines(Rows().Select(r => r.Cells[0]).ToArray()),
            "Ctrl+Delete deletes selected rows exactly once");
        await Key("z", control: true);
        True(Rows().Count == 6, "Undo restores rows deleted by keyboard");

        await ClickCell(1, 1);
        await Key("F2");
        await Key("a", control: true);
        await Key("7");
        await Key("7");
        await Key("Return");
        Equal("77", Rows()[1].Cells[1], "F2 edits the selected cell and Enter commits");
        await Key("z", control: true);
        Equal("10", Rows()[1].Cells[1], "Undo restores an edited cell");
        await ClickCell(0, 0);
        await Key("q");
        await Key("Return");
        Equal("q", Rows()[0].Cells[0], "typing replaces a selected cell and commits");
        await Key("z", control: true);
        Equal("Delta", Rows()[0].Cells[0], "Undo restores typed replacement");

        await ClickCell(1, 1);
        await ClickCell(2, 2, shift: true);
        var copied = SelectedText();
        await _window.Clipboard!.SetTextAsync("baseline");
        bool clipboardAvailable = await _window.Clipboard.GetTextAsync() == "baseline";
        if (clipboardAvailable)
        {
            await Key("c", control: true);
            Equal(Lines("10\tblue", "30\tred"), await _window.Clipboard.GetTextAsync() ?? "<null>",
                "native clipboard receives only the rectangular cell selection");
        }
        else Console.WriteLine("SKIP native clipboard roundtrip: direct platform SetText/GetText fails on this display.");
        await ClickCell(3, 1);
        if (clipboardAvailable) await Key("v", control: true);
        else typeof(MainWindow).GetMethod("PasteCellsText", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_window, new object[] { copied });
        Equal("10\tblue", string.Join('\t', Rows()[3].Cells.Skip(1)), "paste inserts clipboard rectangle at selected cell");
        Equal("30\tred", string.Join('\t', Rows()[4].Cells.Skip(1)), "paste preserves all rows of the copied rectangle");
        await Key("z", control: true);
        Equal("20\tgreen", string.Join('\t', Rows()[3].Cells.Skip(1)), "Undo restores overwritten paste values");

        var filter = _window.FindControl<TextBox>("FilterBox")!;
        await Click(filter);
        await Key("a");
        await WaitUntil(() => Rows().Count == 4);
        await ClickCell(0, 1);
        await Key("space", control: true);
        Equal(Lines("40", "10", "30", "20"), SelectedText(), "whole-column selection respects the visible filtered rows");
        await Click(_window.FindControl<Button>("ShowAllButton")!);

        await CheckScopedFindReplace();
        await CheckSelectedTransforms();

        var largePath = Path.Combine(_directory, "long.csv");
        File.WriteAllText(largePath, "Name,Value,Group\n" + string.Join('\n', Enumerable.Range(0, 200).Select(i => $"Item{i},{i},g{i}")));
        await _window.LoadFileAsync(largePath);
        await Pause();
        var presenter = _grid.GetVisualDescendants().OfType<DataGridRowsPresenter>().First();
        var start = Cell(0, 1).PointToScreen(new Point(20, 12));
        var beyond = presenter.PointToScreen(new Point(200, presenter.Bounds.Height + 12));
        _input.Move(start);
        await Pause();
        _input.Button(true);
        await Task.Delay(150);
        _input.Move(beyond);
        await Task.Delay(900);
        _input.Button(false);
        await Pause();
        True(SelectedText().Split(Environment.NewLine).Length > 20, "dragging below the viewport scrolls and extends the cell range");
        await Key("Home", control: true);
        await Pause();
        await ClickCell(1, 1);
        await Key("BackSpace", control: true);
        True(Rows().Count == 199 && Rows()[1].Cells[0] == "Item2", "Ctrl+Backspace deletes a row once despite the menu shortcut");
        await Key("z", control: true);
        True(Rows().Count == 200, "Undo restores the Ctrl+Backspace deletion");
    }

    private static async Task CheckScopedFindReplace()
    {
        var path = Path.Combine(_directory, "search-scope.csv");
        File.WriteAllText(path, "Name,Value,Group\nFirst,red,red\nSecond,red,red\nThird,red,red\nFourth,red,red\n");
        await _window.LoadFileAsync(path);
        await Pause();
        await ClickCell(1, 2);
        await ClickCell(2, 2, shift: true);
        await Key("f", control: true, alt: true);
        var find = ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).Windows
            .OfType<FindReplaceWindow>().Single();
        T Named<T>(string name) where T : Control => find.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
        Button Button(string label) => find.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, label));
        string Status() => Named<TextBlock>("SearchStatus").Text ?? "";
        await Key("r");
        await Key("e");
        await Key("d");
        await Click(Named<ComboBox>("SearchScope"));
        await Key("Down");
        await Key("Down");
        await Key("Return");
        True(Named<ComboBox>("SearchScope").SelectedIndex == 2, "scope dropdown selects Selected cells through keyboard input");
        await Click(Button("Count"));
        Equal("2 matching cells.", Status(), "selected-cell search excludes matching neighboring columns and rows");
        await Click(Button("Find Next"));
        Equal("red", SelectedText(), "Find Next moves the cell cursor within the captured selection");
        True(Status().Contains("row 2"), "selected search wraps from its original last cell to its first match");
        await Click(Button("Count"));
        Equal("2 matching cells.", Status(), "finding a single match does not shrink the captured search region");
        await Click(Button("Find Next"));
        True(Status().Contains("row 3"), "repeated Find Next visits the next cell in the captured selection");
        await Click(Button("Find Next"));
        True(Status().Contains("row 2"), "Find Next wraps only within the captured selection");
        await Click(Named<TextBox>("ReplaceText"));
        await Key("x");
        await Click(Button("Replace"));
        True(Rows()[1].Cells[2] == "x" && Rows()[2].Cells[2] == "red" && Rows()[1].Cells[1] == "red",
            "Replace changes one matching selected cell and preserves its neighboring values");
        await ClickCell(0, 0);
        await Key("z", control: true);
        Equal("red", Rows()[1].Cells[2], "Undo restores a scoped single replacement");
        await Click(Button("Replace All"));
        Equal("Replaced in 2 cells.", Status(), "Replace All uses the captured region after cursor and grid selection changes");
        True(Rows()[0].Cells[2] == "red" && Rows()[1].Cells[2] == "x" && Rows()[2].Cells[2] == "x" &&
             Rows()[3].Cells[2] == "red" && Rows().All(r => r.Cells[1] == "red"),
            "scoped Replace All preserves every unselected row and column");
        await ClickCell(0, 0);
        await Key("z", control: true);
        True(Rows().All(r => r.Cells[2] == "red"), "one Undo restores every scoped replacement");
        await Key("z", control: true, shift: true);
        True(Rows()[1].Cells[2] == "x" && Rows()[2].Cells[2] == "x" && Rows()[0].Cells[2] == "red",
            "Redo reapplies only the captured cell changes");
        await Key("z", control: true);

        // Captured row identities remain meaningful when sorting changes their visible positions.
        await ClickHeader(0);
        await WaitUntil(() => Rows()[1].Cells[0] == "Fourth");
        await Click(Button("Replace All"));
        True(Rows().All(r => r.Cells[2] == (r.Cells[0] is "Second" or "Third" ? "x" : "red")),
            "sorting does not redirect a captured selection to different records");
        await ClickCell(0, 0);
        await Key("z", control: true);

        var filter = _window.FindControl<TextBox>("FilterBox")!;
        await Click(filter);
        foreach (char c in "Second") await Key(char.ToLowerInvariant(c).ToString());
        await WaitUntil(() => Rows().Count == 1);
        await Click(Button("Count"));
        Equal("1 matching cell.", Status(), "selected search intersects its captured region with currently visible rows");
        await Click(Button("Replace All"));
        Equal("Replaced in 1 cell.", Status(), "selected replacement does not change captured rows hidden by a filter");
        await Click(_window.FindControl<Button>("ShowAllButton")!);
        True(Rows().Single(r => r.Cells[0] == "Second").Cells[2] == "x" &&
             Rows().Single(r => r.Cells[0] == "Third").Cells[2] == "red",
            "a hidden matching record remains unchanged after scoped replacement");

        await _window.LoadFileAsync(path);
        await Pause();
        await Click(Button("Replace All"));
        True(Status().Contains("table changed") && Rows().All(r => r.Cells[2] == "red"),
            "a captured region from an old document is rejected without changing data");
        await Click(Named<Button>("UseSelection"));
        await Click(Button("Replace All"));
        True(Status().Contains("Select cells") && Rows().All(r => r.Cells[2] == "red"),
            "an empty selected scope never falls back to replacing the whole table");
        await ClickCell(0, 2);
        await Click(Named<Button>("UseSelection"));
        await Click(Button("Count"));
        Equal("1 matching cell.", Status(), "Use current selection explicitly captures a new search region");

        await ClickCell(0, 1);
        await Click(Named<ComboBox>("SearchScope"));
        await Key("Home");
        await Key("Down");
        await Key("Return");
        await Click(Button("Replace All"));
        True(Rows().All(r => r.Cells[1] == "x" && r.Cells[2] == "red"),
            "current-column replacement remains limited to the current visible column");
        await Click(Named<ComboBox>("SearchScope"));
        await Key("Home");
        await Key("Return");
        await Click(Button("Count"));
        Equal("4 matching cells.", Status(), "visible-cell search remains available independently of the captured selection");
        find.Close();
        await Pause();
    }

    private static async Task CheckSelectedTransforms()
    {
        var path = Path.Combine(_directory, "transform-scope.csv");
        File.WriteAllText(path, "Name,Value,Group\nFirst, untouched , untouched \nSecond, keep , red \nThird, keep , blue \nFourth, untouched , untouched \n");
        await _window.LoadFileAsync(path);
        await Pause();
        await ClickCell(1, 2);
        await ClickCell(2, 2, shift: true);
        await ContextAction("Trim Selected Cells", Cell(2, 2));
        True(Rows()[1].Cells[2] == "red" && Rows()[2].Cells[2] == "blue" &&
             Rows()[1].Cells[1] == " keep " && Rows()[0].Cells[2] == " untouched ",
            "context-menu trim changes only the selected rectangle");
        await ContextAction("UPPERCASE Selected Cells", Cell(2, 2));
        True(Rows()[1].Cells[2] == "RED" && Rows()[2].Cells[2] == "BLUE" && Rows()[1].Cells[1] == " keep ",
            "context-menu uppercase preserves selection and unselected values");
        await Key("z", control: true);
        True(Rows()[1].Cells[2] == "red" && Rows()[2].Cells[2] == "blue", "one Undo restores a selected-cell transformation");
        await Key("z", control: true, shift: true);
        True(Rows()[1].Cells[2] == "RED" && Rows()[2].Cells[2] == "BLUE", "Redo restores the whole selected-cell transformation");
        await ContextAction("lowercase Selected Cells", Cell(2, 2));
        True(Rows()[1].Cells[2] == "red" && Rows()[2].Cells[2] == "blue", "context-menu lowercase applies to the retained rectangle");

        var filter = _window.FindControl<TextBox>("FilterBox")!;
        await Click(filter);
        foreach (char c in "blue") await Key(c.ToString());
        await WaitUntil(() => Rows().Count == 1);
        await ClickCell(0, 2);
        await Key("Delete");
        await WaitUntil(() => Rows().Count == 0);
        True(Rows().Count == 0, "clearing selected cells refreshes active search results");
        await Key("z", control: true);
        await WaitUntil(() => Rows().Count == 1);
        Equal("blue", Rows()[0].Cells[2], "Undo restores a changed cell and its matching filtered row");
        await Click(_window.FindControl<Button>("ShowAllButton")!);
        True(Rows().Count == 4 && Rows()[1].Cells[2] == "red" && Rows()[0].Cells[2] == " untouched ",
            "filtering and clearing leave every unselected record unchanged");
    }

    private static async Task ContextAction(string label, Control cell)
    {
        _input.Move(cell.PointToScreen(new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2)));
        _input.Button(true, 3);
        await Task.Delay(30);
        _input.Button(false, 3);
        await Pause();
        var item = _grid.ContextMenu!.GetVisualDescendants().OfType<MenuItem>().Single(m => Equals(m.Header, label));
        await Click(item);
    }

    private static async Task LoadFixture()
    {
        var path = Path.Combine(_directory, "fixture.csv");
        File.WriteAllText(path, Fixture);
        await _window.LoadFileAsync(path);
        await Pause();
    }

    private static List<Row> Rows() => _grid.ItemsSource!.Cast<Row>().ToList();
    private static string SelectedText() => (string)typeof(MainWindow)
        .GetMethod("SelectedCellsText", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_window, null)!;
    private static Control Cell(int row, int column) => _grid.Columns[column].GetCellContent(Rows()[row])!
        .GetVisualAncestors().OfType<DataGridCell>().First();
    private static string Lines(params string[] rows) => string.Join(Environment.NewLine, rows);
    private static Task Pause() => Task.Delay(100);
    private static void Equal(string expected, string actual, string name) => True(expected == actual,
        name + $" (expected {expected.Replace("\n", "|")}; got {actual.Replace("\n", "|")})");
    private static void True(bool value, string name)
    {
        if (!value) throw new InvalidOperationException("FAIL " + name);
        _passed++;
        Console.WriteLine("PASS " + name);
    }

    private static async Task ClickCell(int row, int column, bool shift = false) => await Click(Cell(row, column), shift);
    private static async Task ClickHeader(int column)
    {
        var header = _grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Single(h => h.IsVisible && ReferenceEquals(h.Content, _grid.Columns[column].Header));
        await Click(header);
    }
    private static async Task Click(Control control, bool shift = false)
    {
        _input.Move(control.PointToScreen(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2)));
        if (shift) _input.Modifier("Shift_L", true);
        _input.Button(true);
        await Task.Delay(30);
        _input.Button(false);
        if (shift) _input.Modifier("Shift_L", false);
        await Pause();
    }
    private static async Task Drag(Control first, Control last)
    {
        var start = first.PointToScreen(new Point(first.Bounds.Width / 2, first.Bounds.Height / 2));
        var end = last.PointToScreen(new Point(last.Bounds.Width / 2, last.Bounds.Height / 2));
        _input.Move(start);
        _input.Button(true);
        await Task.Delay(30);
        for (var i = 1; i <= 8; i++)
        {
            _input.Move(new PixelPoint(start.X + (end.X - start.X) * i / 8, start.Y + (end.Y - start.Y) * i / 8));
            await Task.Delay(20);
        }
        _input.Button(false);
        await Pause();
    }
    private static async Task Key(string key, bool control = false, bool shift = false, bool alt = false)
    {
        if (control) _input.Modifier("Control_L", true);
        if (shift) _input.Modifier("Shift_L", true);
        if (alt) _input.Modifier("Alt_L", true);
        _input.Modifier(key, true);
        _input.Modifier(key, false);
        if (alt) _input.Modifier("Alt_L", false);
        if (shift) _input.Modifier("Shift_L", false);
        if (control) _input.Modifier("Control_L", false);
        await Pause();
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            if (condition()) { await Pause(); return; }
            await Task.Delay(50);
        }
        throw new TimeoutException("UI did not reach the expected state.");
    }

    private sealed class NativeInput : IDisposable
    {
        private readonly IntPtr _display = XOpenDisplay(IntPtr.Zero);
        public NativeInput()
        {
            if (_display == IntPtr.Zero) throw new InvalidOperationException("Cannot open X11 display.");
        }
        public void Move(PixelPoint point) { XTestFakeMotionEvent(_display, -1, point.X, point.Y, 0); XFlush(_display); }
        public void Button(bool down, uint button = 1) { XTestFakeButtonEvent(_display, button, down, 0); XFlush(_display); }
        public void Modifier(string key, bool down)
        {
            var code = XKeysymToKeycode(_display, XStringToKeysym(key));
            if (code == 0) throw new InvalidOperationException("No X11 keycode for " + key);
            XTestFakeKeyEvent(_display, code, down, 0); XFlush(_display);
        }
        public void Dispose() => XCloseDisplay(_display);
        [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
        [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
        [DllImport("libX11.so.6")] private static extern int XFlush(IntPtr display);
        [DllImport("libX11.so.6")] private static extern nuint XStringToKeysym(string name);
        [DllImport("libX11.so.6")] private static extern byte XKeysymToKeycode(IntPtr display, nuint symbol);
        [DllImport("libXtst.so.6")] private static extern int XTestFakeMotionEvent(IntPtr display, int screen, int x, int y, nuint delay);
        [DllImport("libXtst.so.6")] private static extern int XTestFakeButtonEvent(IntPtr display, uint button, [MarshalAs(UnmanagedType.Bool)] bool down, nuint delay);
        [DllImport("libXtst.so.6")] private static extern int XTestFakeKeyEvent(IntPtr display, uint keycode, [MarshalAs(UnmanagedType.Bool)] bool down, nuint delay);
    }
}
