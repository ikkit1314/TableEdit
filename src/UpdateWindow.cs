using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace TableEdit;

/// <summary>
/// Software Update dialog: compares the installed version with the latest GitHub release, shows its notes,
/// and downloads, verifies and installs the new version (macOS app), or opens the release page elsewhere.
/// </summary>
public sealed class UpdateWindow : Window
{
    private readonly TextBlock _headline = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly SelectableTextBlock _notes = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly ScrollViewer _notesBox;
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 6, IsVisible = false };
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _autoCheck = new() { Content = "Check for updates automatically" };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
    private CancellationTokenSource? _cancel;
    private ReleaseInfo? _release;

    private UpdateWindow()
    {
        Title = "Software Update";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _autoCheck.IsChecked = Updater.AutoCheck;
        _autoCheck.IsCheckedChanged += (_, _) => Updater.AutoCheck = _autoCheck.IsChecked == true;
        _notesBox = new ScrollViewer
        {
            MaxHeight = 260,
            IsVisible = false,
            Content = new Border
            {
                Padding = new Thickness(10, 8),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
                Child = _notes,
            },
        };
        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children = { _headline, _detail, _notesBox, _progress, _error, _autoCheck, _buttons },
        };
        Closed += (_, _) => _cancel?.Cancel();
    }

    /// <summary>Menu command: always shows the dialog, including "you're up to date" and errors.</summary>
    public static async Task CheckInteractiveAsync(Window owner)
    {
        var window = new UpdateWindow();
        window.ShowChecking();
        var shown = window.ShowDialog(owner);
        try { window.ShowRelease(await Updater.GetLatestAsync()); }
        catch (Exception ex) { window.ShowFailure(ex); }
        await shown;
    }

    /// <summary>Launch check: stays silent unless a newer version that the user hasn't skipped exists.</summary>
    public static async Task CheckQuietlyAsync(Func<Window?> owner)
    {
        ReleaseInfo release;
        try { release = await Updater.GetLatestAsync(); }
        catch { return; }
        if (!Updater.IsNewer(release.Version) || Updater.IsSkipped(release.Version) || owner() is not { } parent) return;
        var window = new UpdateWindow();
        window.ShowRelease(release);
        await window.ShowDialog(parent);
    }

    private Button AddButton(string label, Action click, bool isDefault = false, bool isCancel = false)
    {
        var button = new Button { Content = label, IsDefault = isDefault, IsCancel = isCancel };
        button.Click += (_, _) => click();
        _buttons.Children.Add(button);
        return button;
    }

    private void ShowChecking()
    {
        _headline.Text = "Checking for updates…";
        _detail.Text = $"You have TableEdit {Updater.Current.ToString(3)}.";
        _buttons.Children.Clear();
        AddButton("Cancel", () => Close(), isCancel: true);
    }

    private void ShowFailure(Exception ex)
    {
        _headline.Text = "Couldn't check for updates";
        _detail.Text = $"You have TableEdit {Updater.Current.ToString(3)}.";
        _error.Text = ex is HttpRequestException or TaskCanceledException
            ? "GitHub couldn't be reached. Check your internet connection and try again."
            : ex.Message;
        _buttons.Children.Clear();
        AddButton("Open Releases Page", () => Updater.OpenInBrowser(Updater.ReleasesPage));
        AddButton("OK", () => Close(), isDefault: true, isCancel: true);
    }

    private void ShowRelease(ReleaseInfo release)
    {
        _release = release;
        _error.Text = "";
        _buttons.Children.Clear();
        var current = Updater.Current.ToString(3);
        var latest = release.Version.ToString(3);
        if (!Updater.IsNewer(release.Version))
        {
            _headline.Text = "TableEdit is up to date";
            _detail.Text = release.Version == Updater.Current
                ? $"Version {current} is the latest release."
                : $"You have {current}; the latest release on GitHub is {latest}.";
            AddButton("OK", () => Close(), isDefault: true, isCancel: true);
            return;
        }

        _headline.Text = $"TableEdit {latest} is available";
        var bundle = Updater.InstalledBundle(out var reason);
        _detail.Text = $"You have {current}." + (bundle == null && release.DmgUrl != null ? " " + reason : "");
        _notes.Text = string.IsNullOrWhiteSpace(release.Notes) ? "(No release notes.)" : release.Notes.Trim();
        _notesBox.IsVisible = true;

        AddButton("Skip This Version", () => { Updater.Skip(release.Version); Close(); });
        AddButton("Later", () => Close(), isCancel: true);
        if (release.DmgUrl == null)
            AddButton("Open Release Page", () => { Updater.OpenInBrowser(release.PageUrl); Close(); }, isDefault: true);
        else if (bundle == null)
            AddButton("Download", () => { Updater.OpenInBrowser(release.DmgUrl); Close(); }, isDefault: true);
        else
            AddButton("Download and Install", () => _ = InstallAsync(release, bundle), isDefault: true);
    }

    private async Task InstallAsync(ReleaseInfo release, string bundle)
    {
        _cancel = new CancellationTokenSource();
        var cancel = _cancel.Token;
        _error.Text = "";
        _buttons.Children.Clear();
        AddButton("Cancel", () => { _cancel.Cancel(); }, isCancel: true);
        _progress.IsVisible = true;
        _progress.IsIndeterminate = false;
        _progress.Value = 0;
        _headline.Text = $"Downloading TableEdit {release.Version.ToString(3)}…";

        string staged;
        try
        {
            var progress = new Progress<double>(p => _progress.Value = p);
            var dmg = await Updater.DownloadAsync(release, progress, cancel);
            _headline.Text = "Preparing the update…";
            _progress.IsIndeterminate = true;
            staged = await Updater.StageAsync(dmg, bundle, release.Version, cancel);
        }
        catch (Exception ex)
        {
            _progress.IsVisible = false;
            if (cancel.IsCancellationRequested) { ShowRelease(release); return; }
            ShowRelease(release);
            _headline.Text = "The update couldn't be installed";
            _error.Text = ex is HttpRequestException ? "The download failed. Check your internet connection and try again." : ex.Message;
            return;
        }

        _progress.IsVisible = false;
        _headline.Text = $"TableEdit {release.Version.ToString(3)} is ready to install";
        _detail.Text = "TableEdit will quit, replace itself with the new version and open again.";
        _buttons.Children.Clear();
        AddButton("Later", () => Close(), isCancel: true);
        AddButton("Install and Relaunch", () => Relaunch(staged, bundle), isDefault: true);
    }

    private void Relaunch(string staged, string bundle)
    {
        var unsaved = MainWindow.UnsavedDocumentNames();
        if (unsaved.Count > 0)
        {
            _error.Text = "Save or close these documents first, then click Install and Relaunch again: " + string.Join(", ", unsaved);
            return;
        }
        try { Updater.LaunchSwapAndRelaunch(staged, bundle); }
        catch (Exception ex)
        {
            _error.Text = "Couldn't start the installer: " + ex.Message;
            return;
        }
        Close();
        Dispatcher.UIThread.Post(() =>
        {
            MainWindow.CloseAllForUpdate();
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        });
    }
}
