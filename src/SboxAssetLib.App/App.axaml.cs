using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SboxAssetLib.App.Services;
using SboxAssetLib.App.ViewModels;
using SboxAssetLib.App.Views;

namespace SboxAssetLib.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new AppServices();
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(services),
            };
            desktop.Exit += (_, _) => services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
