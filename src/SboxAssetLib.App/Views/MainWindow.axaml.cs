using Avalonia.Controls;
using Avalonia.Platform.Storage;
using SboxAssetLib.App.ViewModels;

namespace SboxAssetLib.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WirePicker();
    }

    private void WirePicker()
    {
        if (DataContext is not MainViewModel vm)
            return;

        vm.PickFolder = async () =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose your asset library folder",
                AllowMultiple = false,
            });
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        };
    }
}
