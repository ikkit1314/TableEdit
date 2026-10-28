using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace TableEdit;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            var window = new MainWindow();
            desktop.MainWindow = window;

            foreach (var file in desktop.Args?.Where(a => !a.StartsWith("-") && File.Exists(a)) ?? [])
                MainWindow.OpenInBestWindow(file);

            // macOS delivers Finder "Open With" / double-click through activation events.
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            {
                activatable.Activated += (_, e) =>
                {
                    if (e is FileActivatedEventArgs files)
                    {
                        var paths = files.Files.Select(item => item.TryGetLocalPath()).OfType<string>().ToArray();
                        Dispatcher.UIThread.Post(() =>
                        {
                            foreach (var path in paths) MainWindow.OpenInBestWindow(path);
                        });
                    }
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
