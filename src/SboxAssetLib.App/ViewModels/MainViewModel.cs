using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SboxAssetLib.App.Services;
using SboxAssetLib.Core.Bridge;
using SboxAssetLib.Core.Download;
using SboxAssetLib.Core.Import;
using SboxAssetLib.Core.Model;
using SboxAssetLib.Core.Providers;

namespace SboxAssetLib.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AppServices _svc;
    private int _page;
    private bool _suppressSearch;

    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private IAssetProvider _selectedProvider;
    // Nullable: when the Kind list is rebuilt the ComboBox transiently has no selection, and a
    // non-nullable AssetKind would throw InvalidCastException trying to bind null back.
    [ObservableProperty] private AssetKind? _selectedKind = AssetKind.Texture;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _pbrOnly;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Search a CC0 library to begin.";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private AssetCardViewModel? _selected;
    [ObservableProperty] private string? _selectedResolution;
    [ObservableProperty] private string _libraryPath;
    [ObservableProperty] private bool _bridgeConnected;
    [ObservableProperty] private string _bridgeStatus = "s&box: checking…";

    public MainViewModel(AppServices svc)
    {
        _svc = svc;
        _selectedProvider = svc.Providers[0];
        _libraryPath = svc.Settings.LibraryPath;
        RebuildKinds();
        _ = CheckBridgeAsync();

        // Auto-populate on open (same as hitting Search with no term). An optional env var seeds the box.
        SearchText = Environment.GetEnvironmentVariable("SBOXLIB_DEMO_QUERY") ?? "";
        _ = SearchAsync();
    }

    public ObservableCollection<AssetCardViewModel> Results { get; } = [];
    public IReadOnlyList<IAssetProvider> Providers => _svc.Providers;
    public ObservableCollection<AssetKind> Kinds { get; } = [];
    public ObservableCollection<string> Resolutions { get; } = [];

    public bool HasSelection => Selected is not null;

    /// <summary>Set by the view: opens a native folder picker and returns the chosen path.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    partial void OnSelectedChanged(AssetCardViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        if (value is not null)
            _ = LoadDetailAsync(value);
    }

    partial void OnSelectedProviderChanged(IAssetProvider value)
    {
        RebuildKinds();
        RefreshIfSearching();
    }

    partial void OnSelectedKindChanged(AssetKind? value) => RefreshIfSearching();

    /// <summary>Constrain the Kind selector to what the current provider supports.</summary>
    private void RebuildKinds()
    {
        _suppressSearch = true;
        var previous = SelectedKind;
        Kinds.Clear();
        foreach (var k in SelectedProvider.SupportedKinds)
            Kinds.Add(k);
        SelectedKind = previous is { } p && Kinds.Contains(p)
            ? p
            : Kinds.Count > 0 ? Kinds[0] : null;
        _suppressSearch = false;
    }

    private void RefreshIfSearching()
    {
        if (!_suppressSearch && (Results.Count > 0 || !string.IsNullOrWhiteSpace(SearchText)))
            _ = SearchAsync();
    }

    partial void OnLibraryPathChanged(string value)
    {
        _svc.Settings.LibraryPath = value;
        _svc.Settings.Save();
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        _page = 0;
        Results.Clear();
        Selected = null;
        await RunSearchAsync();
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (!HasMore || IsBusy)
            return;
        _page++;
        await RunSearchAsync();
    }

    private async Task RunSearchAsync()
    {
        try
        {
            IsBusy = true;
            Status = "Searching…";
            var query = new AssetQuery
            {
                Text = SearchText,
                Kind = SelectedKind,
                PbrOnly = PbrOnly,
                Page = _page,
                PageSize = 60,
            };
            var page = await SelectedProvider.SearchAsync(query);
            foreach (var item in page.Items)
                Results.Add(new AssetCardViewModel(item, _svc.FindProvider(item.ProviderId), _svc));
            HasMore = page.HasMore;
            Status = $"{page.Total} result(s) in {SelectedProvider.DisplayName} · {SelectedKind}"
                     + (HasMore ? " · load more" : "");
        }
        catch (Exception ex)
        {
            Status = "Search failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadDetailAsync(AssetCardViewModel card)
    {
        try
        {
            if (card.Detail is null)
            {
                var detail = await card.Provider.GetDetailAsync(card.Summary.Id, card.Summary.Kind);
                card.Detail = detail;
                card.Maps = detail.MapSupport;
            }

            Resolutions.Clear();
            foreach (var r in card.Detail!.Resolutions)
                Resolutions.Add(r);
            SelectedResolution = Resolutions.Contains(_svc.Settings.DefaultResolution)
                ? _svc.Settings.DefaultResolution
                : Resolutions.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Status = "Failed to load details: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task AddToLibraryAsync()
    {
        if (Selected?.Detail is null)
        {
            Status = "Select an asset first.";
            return;
        }
        if (string.IsNullOrWhiteSpace(LibraryPath))
        {
            Status = "Set a library folder first.";
            return;
        }
        var res = SelectedResolution ?? Selected.Detail.Resolutions.FirstOrDefault() ?? "2k";
        await InstallAsync(Selected.Detail, res,
            new InstallOptions { AddonRoot = LibraryPath, Prefs = _svc.Settings.Prefs }, toLibrary: true);
    }

    [RelayCommand]
    private async Task ImportToSboxAsync()
    {
        if (Selected?.Detail is null)
        {
            Status = "Select an asset first.";
            return;
        }
        var status = await _svc.Bridge.PingAsync();
        if (status?.ContentPath is null)
        {
            Status = "s&box plugin not connected — open s&box with the plugin installed.";
            return;
        }
        var res = SelectedResolution ?? Selected.Detail.Resolutions.FirstOrDefault() ?? "2k";
        var installed = await InstallAsync(Selected.Detail, res,
            new InstallOptions { AddonRoot = status.ContentPath, Prefs = _svc.Settings.Prefs, WriteManifest = false },
            toLibrary: false);
        if (installed is null)
            return;

        Status = "Compiling in s&box…";
        var result = await _svc.Bridge.ImportAsync(new ImportRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            PrimaryAsset = installed.PrimaryAsset,
            Kind = installed.Kind.ToString(),
            SpawnInScene = installed.Kind == AssetKind.Model,
        });
        Status = result.Ok
            ? $"Imported {installed.Id} into s&box ({status.ProjectName})."
            : "s&box import failed: " + result.Error;
    }

    private async Task<InstalledAsset?> InstallAsync(AssetDetail detail, string res, InstallOptions opts, bool toLibrary)
    {
        try
        {
            IsBusy = true;
            Progress = 0;
            Status = $"Downloading {detail.Summary.Id} @ {res}…";
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (p.Fraction is { } f)
                    Dispatcher.UIThread.Post(() => Progress = f * 100);
            });
            var provider = Selected?.Provider ?? _svc.FindProvider(detail.Summary.ProviderId);
            var installed = await _svc.Installer.InstallAsync(provider, detail, res, opts, progress);
            Progress = 100;
            Status = toLibrary
                ? $"Added to library: {installed.RelativeDir}"
                : $"Installed: {installed.PrimaryAsset}";
            return installed;
        }
        catch (Exception ex)
        {
            Status = "Install failed: " + ex.Message;
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenSource()
    {
        var url = Selected?.SourceUrl;
        if (string.IsNullOrWhiteSpace(url))
            return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Status = "Couldn't open page: " + ex.Message; }
    }

    [RelayCommand]
    private async Task BrowseLibraryAsync()
    {
        if (PickFolder is null)
            return;
        var picked = await PickFolder();
        if (!string.IsNullOrWhiteSpace(picked))
            LibraryPath = picked;
    }

    [RelayCommand]
    private async Task CheckBridgeAsync()
    {
        var status = await _svc.Bridge.PingAsync();
        BridgeConnected = status?.Ok == true;
        BridgeStatus = BridgeConnected ? $"s&box: {status!.ProjectName ?? "connected"}" : "s&box: not connected";
    }
}
