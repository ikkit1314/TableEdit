using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TableEdit;

/// <summary>Small code-built dialogs so the app has no extra XAML windows to keep in sync.</summary>
public static class Dialogs
{
    private static Window MakeWindow(string title, double width) => new()
    {
        Title = title,
        Width = width,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ShowInTaskbar = false,
    };

    private static StackPanel ButtonRow(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var b in buttons) row.Children.Add(b);
        return row;
    }

    public static async Task<string?> Prompt(Window owner, string title, string label, string initial = "")
    {
        var dlg = MakeWindow(title, 380);
        var box = new TextBox { Text = initial };
        var ok = new Button { Content = "OK", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        ok.Click += (_, _) => dlg.Close(box.Text ?? "");
        cancel.Click += (_, _) => dlg.Close(null);
        dlg.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 8,
            Children = { new TextBlock { Text = label }, box, ButtonRow(cancel, ok) },
        };
        dlg.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        return await dlg.ShowDialog<string?>(owner);
    }

    /// <summary>Shows a message with the given buttons; returns the chosen label, or null when dismissed.</summary>
    public static async Task<string?> Ask(Window owner, string title, string message, params string[] buttons)
    {
        var dlg = MakeWindow(title, 420);
        var btns = buttons.Select((label, i) =>
        {
            var b = new Button { Content = label, IsDefault = i == buttons.Length - 1, IsCancel = label == "Cancel" };
            b.Click += (_, _) => dlg.Close(label);
            return b;
        }).ToArray();
        dlg.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, ButtonRow(btns) },
        };
        return await dlg.ShowDialog<string?>(owner);
    }

    public static Task Info(Window owner, string title, string message) => Ask(owner, title, message, "OK");

    public static void ShowText(Window owner, string title, string text)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 520,
            Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer
            {
                Content = new SelectableTextBlock
                {
                    Text = text,
                    Margin = new Thickness(16),
                    FontFamily = new FontFamily("Menlo, Consolas, monospace"),
                    FontSize = 13,
                },
            },
        };
        dlg.Show(owner);
    }
}
