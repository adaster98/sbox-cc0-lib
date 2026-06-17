using SboxAssetLib.Core.Model;

namespace SboxAssetLib.Core.Providers;

/// <summary>
/// A virtual "All sources" provider that fans a search out to every real provider and interleaves
/// the results. Detail/resolve are never routed through here — the UI keeps each result's owning
/// provider (via <see cref="AssetSummary.ProviderId"/>) and calls that one directly.
/// </summary>
public sealed class CompositeProvider : IAssetProvider, IStreamingSearchProvider
{
    private readonly IReadOnlyList<IAssetProvider> _providers;

    public CompositeProvider(IReadOnlyList<IAssetProvider> providers) => _providers = providers;

    public string Id => "all";
    public string DisplayName => "All sources";

    public IReadOnlyList<AssetKind> SupportedKinds { get; } =
        Enum.GetValues<AssetKind>(); // union — individual providers filter what they actually return

    public async Task<SearchPage> SearchAsync(AssetQuery query, CancellationToken ct = default)
    {
        var kind = query.Kind ?? AssetKind.Texture;
        var applicable = _providers.Where(p => p.SupportedKinds.Contains(kind)).ToList();
        if (applicable.Count == 0)
            return new SearchPage { Items = [], Page = query.Page, HasMore = false, Total = 0 };

        var pages = await Task.WhenAll(applicable.Select(p => SafeSearchAsync(p, query, ct))).ConfigureAwait(false);

        var merged = Interleave(pages.Select(p => p.Items).ToList()).ToList();
        return new SearchPage
        {
            Items = merged,
            Page = query.Page,
            HasMore = pages.Any(p => p.HasMore),
            Total = pages.Sum(p => p.Total ?? p.Items.Count),
        };
    }

    /// <summary>
    /// Streaming variant: fan out to every applicable provider and surface each page as it finishes
    /// so the UI shows fast sources immediately and slow ones backfill. <see cref="SafeSearchAsync"/>
    /// keeps one failing source from blanking the whole search.
    /// </summary>
    public async Task SearchAsync(AssetQuery query, Func<SearchPage, Task> onPage, CancellationToken ct = default)
    {
        var kind = query.Kind ?? AssetKind.Texture;
        var pending = _providers
            .Where(p => p.SupportedKinds.Contains(kind))
            .Select(p => SafeSearchAsync(p, query, ct))
            .ToList();

        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(done);
            ct.ThrowIfCancellationRequested();
            await onPage(done.Result).ConfigureAwait(false);
        }
    }

    // Routed by the UI to the originating provider, so these are never expected to run.
    public Task<AssetDetail> GetDetailAsync(string assetId, AssetKind kind, CancellationToken ct = default) =>
        throw new NotSupportedException("Route detail calls to the asset's originating provider.");

    public Task<IReadOnlyList<DownloadFile>> ResolveFilesAsync(
        string assetId, AssetKind kind, string resolution, FormatPrefs prefs, CancellationToken ct = default) =>
        throw new NotSupportedException("Route file resolution to the asset's originating provider.");

    private static async Task<SearchPage> SafeSearchAsync(IAssetProvider p, AssetQuery q, CancellationToken ct)
    {
        try
        {
            return await p.SearchAsync(q, ct).ConfigureAwait(false);
        }
        catch
        {
            // One source failing shouldn't blank the whole "All" search.
            return new SearchPage { Items = [], Page = q.Page, HasMore = false, Total = 0 };
        }
    }

    private static IEnumerable<AssetSummary> Interleave(IReadOnlyList<IReadOnlyList<AssetSummary>> lists)
    {
        var max = lists.Count == 0 ? 0 : lists.Max(l => l.Count);
        for (var i = 0; i < max; i++)
            foreach (var list in lists)
                if (i < list.Count)
                    yield return list[i];
    }
}
