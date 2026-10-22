using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace TableEdit;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;

            var file = desktop.Args?.FirstOrDefault(a => !a.StartsWith("-") && File.Exists(a));
            if (file != null)
                _ = window.LoadFileAsync(file);

            // macOS delivers Finder "Open With" / double-click through activation events.
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            {
                activatable.Activated += (_, e) =>
                {
                    if (e is FileActivatedEventArgs files)
                        foreach (var item in files.Files)
                            if (item.TryGetLocalPath() is { } path)
                                MainWindow.OpenInBestWindow(path);
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
