using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SboxAssetLib.App.Services;
using SboxAssetLib.Core.Model;
using SboxAssetLib.Core.Providers;

namespace SboxAssetLib.App.ViewModels;

/// <summary>One gallery card. Loads its thumbnail and PBR map support lazily/throttled.</summary>
public partial class AssetCardViewModel : ObservableObject
{
    private readonly AppServices _svc;
    private readonly IAssetProvider _provider;

    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private MapType _maps;
    [ObservableProperty] private AssetDetail? _detail;

    public AssetCardViewModel(AssetSummary summary, IAssetProvider provider, AppServices svc)
    {
        Summary = summary;
        _provider = provider;
        _svc = svc;
        _maps = summary.MapSupport;
        _ = LoadThumbnailAsync();
        _ = LoadMapsAsync();
    }

    public AssetSummary Summary { get; }

    /// <summary>The real provider this result came from (used for detail/resolve/install).</summary>
    public IAssetProvider Provider => _provider;

    public string Name => Summary.Name;
    public string Id => Summary.Id;
    public string ProviderName => _provider.DisplayName;
    public string? MaxResolution => Summary.MaxResolution;
    public bool IsModel => Summary.Kind == AssetKind.Model;
    public bool IsPbr => Maps.IsPbr();
    public IReadOnlyList<string> Badges => BuildBadges(Maps);

    // Prefer tags from the loaded detail: some providers (e.g. TextureCan) only carry tags on the
    // detail page, so the search summary's list is empty until LoadDetailAsync runs.
    public IReadOnlyList<string> Tags =>
        Detail?.Summary.Tags is { Count: > 0 } detailTags ? detailTags : Summary.Tags;

    public string AuthorsText => Detail is { Authors.Count: > 0 } d ? "by " + string.Join(", ", d.Authors) : "";
    public string? SourceUrl => Detail?.SourceUrl;
    public string LicenseText => $"License: {Detail?.License ?? "CC0"}";

    partial void OnMapsChanged(MapType value)
    {
        OnPropertyChanged(nameof(IsPbr));
        OnPropertyChanged(nameof(Badges));
    }

    partial void OnDetailChanged(AssetDetail? value)
    {
        OnPropertyChanged(nameof(AuthorsText));
        OnPropertyChanged(nameof(SourceUrl));
        OnPropertyChanged(nameof(LicenseText));
        OnPropertyChanged(nameof(Tags));
    }

    private static readonly (MapType Map, string Label)[] BadgeDefs =
    [
        (MapType.Albedo, "ALB"),
        (MapType.Normal, "NRM"),
        (MapType.Roughness, "RGH"),
        (MapType.Metalness, "MTL"),
        (MapType.AmbientOcclusion, "AO"),
        (MapType.Height, "H"),
        (MapType.Emission, "EM"),
    ];

    private static IReadOnlyList<string> BuildBadges(MapType maps) =>
        BadgeDefs.Where(b => maps.HasFlag(b.Map)).Select(b => b.Label).ToList();

    private async Task LoadThumbnailAsync()
    {
        try
        {
            var path = await _svc.Thumbnails.GetLocalPathAsync(Summary.ThumbnailUrl).ConfigureAwait(false);
            if (path is null)
                return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                try { Thumbnail = new Bitmap(path); }
                catch { /* corrupt/partial cache entry — ignore */ }
            });
        }
        catch { /* network hiccup — leave placeholder */ }
    }

    private async Task LoadMapsAsync()
    {
        if (Maps != MapType.None || Summary.Kind == AssetKind.Model)
            return;
        await _svc.MapLoadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var m = await _provider.GetMapSupportAsync(Summary.Id, Summary.Kind).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => Maps = m);
        }
        catch { /* badges just stay empty */ }
        finally { _svc.MapLoadGate.Release(); }
    }
}
