using SboxAssetLib.Core.Model;

namespace SboxAssetLib.Core.Providers;

/// <summary>
/// Optional capability for providers that can deliver results incrementally instead of as one
/// batched <see cref="SearchPage"/>. Implemented by <see cref="CompositeProvider"/> so the UI can
/// render fast sources immediately and let slow ones backfill, rather than blocking on the slowest.
/// </summary>
public interface IStreamingSearchProvider
{
    /// <summary>
    /// Run the search and invoke <paramref name="onPage"/> once per underlying page as it arrives
    /// (completion order). Completes when every page has been delivered.
    /// </summary>
    Task SearchAsync(AssetQuery query, Func<SearchPage, Task> onPage, CancellationToken ct = default);
}
