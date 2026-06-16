using SboxAssetLib.Core.Model;

namespace SboxAssetLib.Core.Providers;

/// <summary>
/// A pluggable CC0 asset source. Every store (Poly Haven, ambientCG, cgbookcase,
/// sharetextures) implements this so the UI and importer stay provider-agnostic.
/// </summary>
public interface IAssetProvider
{
    /// <summary>Stable, lowercase identifier used in cache paths, e.g. "polyhaven".</summary>
    string Id { get; }

    /// <summary>Human-readable name for the UI, e.g. "Poly Haven".</summary>
    string DisplayName { get; }

    /// <summary>Asset kinds this provider can return.</summary>
    IReadOnlyList<AssetKind> SupportedKinds { get; }

    Task<SearchPage> SearchAsync(AssetQuery query, CancellationToken ct = default);

    Task<AssetDetail> GetDetailAsync(string assetId, AssetKind kind, CancellationToken ct = default);

    /// <summary>
    /// Cheap lookup of which PBR channels an asset provides, for lazily populating gallery
    /// badges. Default implementation derives it from <see cref="GetDetailAsync"/>; providers
    /// may override with something lighter.
    /// </summary>
    async Task<MapType> GetMapSupportAsync(string assetId, AssetKind kind, CancellationToken ct = default)
        => (await GetDetailAsync(assetId, kind, ct).ConfigureAwait(false)).MapSupport;

    /// <summary>
    /// Resolve the concrete files to download for an asset at a given resolution,
    /// honouring the supplied format preferences.
    /// </summary>
    Task<IReadOnlyList<DownloadFile>> ResolveFilesAsync(
        string assetId,
        AssetKind kind,
        string resolution,
        FormatPrefs prefs,
        CancellationToken ct = default);
}
