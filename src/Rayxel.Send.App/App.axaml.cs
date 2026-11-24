using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Rayxel.Send.App;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var model = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = model };
            // Only the window goes: the sender keeps running, and keeps streaming if it was.
            desktop.Exit += (_, _) => model.Dispose();
            model.Start();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
