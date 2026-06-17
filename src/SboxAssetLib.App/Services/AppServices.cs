using System.Net.Http;
using SboxAssetLib.Core.Download;
using SboxAssetLib.Core.Import;
using SboxAssetLib.Core.Providers;
using SboxAssetLib.Core.Providers.AmbientCg;
using SboxAssetLib.Core.Providers.Cc0Textures;
using SboxAssetLib.Core.Providers.PolyHaven;
using SboxAssetLib.Core.Providers.TextureCan;

namespace SboxAssetLib.App.Services;

/// <summary>Composition root: owns the shared HttpClient, providers, downloader, installer and caches.</summary>
public sealed class AppServices : IDisposable
{
    public AppSettings Settings { get; }
    public HttpClient Http { get; }

    /// <summary>The real, individually-selectable providers (no "All" entry).</summary>
    public IReadOnlyList<IAssetProvider> RealProviders { get; }

    /// <summary>What the UI offers: an "All sources" composite followed by the real providers.</summary>
    public IReadOnlyList<IAssetProvider> Providers { get; }

    public DownloadManager Downloader { get; }
    public AssetInstaller Installer { get; }
    public ThumbnailCache Thumbnails { get; }
    public SboxBridgeClient Bridge { get; }

    /// <summary>Throttles the lazy per-card map lookups so a search doesn't fan out unbounded.</summary>
    public SemaphoreSlim MapLoadGate { get; } = new(6);

    public AppServices()
    {
        Settings = AppSettings.Load();

        Http = new HttpClient();
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(Settings.UserAgent);

        // cgbookcase + sharetextures are deferred: their full-res downloads are click/wait-gated
        // (token-signed CDN / Google Drive) and need a headless-browser pass — see CgBookcaseProvider.
        RealProviders =
        [
            new PolyHavenProvider(Http),
            new AmbientCgProvider(Http),
            new Cc0TexturesProvider(Http),
            new TextureCanProvider(Http),
        ];
        Providers = [new CompositeProvider(RealProviders), .. RealProviders];
        Downloader = new DownloadManager(Http);
        Installer = new AssetInstaller(Downloader);
        Thumbnails = new ThumbnailCache(Http);
        Bridge = new SboxBridgeClient();
    }

    /// <summary>Find the real provider that owns a result, by its <c>ProviderId</c>.</summary>
    public IAssetProvider FindProvider(string providerId) =>
        RealProviders.FirstOrDefault(p => p.Id == providerId) ?? RealProviders[0];

    public void Dispose()
    {
        Http.Dispose();
        MapLoadGate.Dispose();
    }
}
