using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace TableEdit;

public sealed record SearchOptions(string Find, string Replace, bool MatchCase, bool WholeCell, bool UseRegex, bool CurrentColumnOnly)
{
    /// <summary>Returns null when the pattern is an invalid regex.</summary>
    public Regex? BuildRegex()
    {
        var pattern = UseRegex ? Find : Regex.Escape(Find);
        if (WholeCell) pattern = "^(?:" + pattern + ")$";
        try
        {
            return new Regex(pattern, MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>Modeless find/replace panel bound to one document window.</summary>
public sealed class FindReplaceWindow : Window
{
    private readonly MainWindow _owner;
    private readonly TextBox _find = new() { Watermark = "Find" };
    private readonly TextBox _replace = new() { Watermark = "Replace with" };
    private readonly CheckBox _case = new() { Content = "Match case" };
    private readonly CheckBox _whole = new() { Content = "Whole cell" };
    private readonly CheckBox _regex = new() { Content = "Regular expression" };
    private readonly CheckBox _column = new() { Content = "Current column only" };
    private readonly TextBlock _status = new() { Opacity = 0.7 };

    public FindReplaceWindow(MainWindow owner)
    {
        _owner = owner;
        Title = "Find & Replace";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        foreach (var box in new[] { _case, _whole, _regex, _column })
            box.Margin = new Thickness(0, 0, 12, 0);

        var next = new Button { Content = "Find Next", IsDefault = true };
        var count = new Button { Content = "Count" };
        var replaceOne = new Button { Content = "Replace" };
        var replaceAll = new Button { Content = "Replace All" };

        next.Click += (_, _) => Report(_owner.FindNext(Options()));
        count.Click += (_, _) => Report(_owner.CountMatches(Options()));
        replaceOne.Click += (_, _) => Report(_owner.ReplaceCurrent(Options()));
        replaceAll.Click += (_, _) => Report(_owner.ReplaceAll(Options()));

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 8,
            Children =
            {
                _find,
                _replace,
                new WrapPanel { Children = { _case, _whole, _regex, _column } },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { count, replaceOne, replaceAll, next },
                },
                _status,
            },
        };

        Opened += (_, _) => _find.Focus();
    }

    private SearchOptions Options() => new(
        _find.Text ?? "",
        _replace.Text ?? "",
        _case.IsChecked == true,
        _whole.IsChecked == true,
        _regex.IsChecked == true,
        _column.IsChecked == true);

    private void Report(string message) => _status.Text = message;
}
