using System.Text.Json;
using SboxAssetLib.Core.Import;
using SboxAssetLib.Core.Model;

namespace SboxAssetLib.Core.Providers.CgBookcase;

/// <summary>
/// Adapter for cgbookcase.com. DEFERRED / not wired into the app yet — kept as a foundation for
/// a future headless-browser download pass (see README "Adding cgbookcase / sharetextures").
///
/// Research notes:
///  • Metadata is a clean full-catalogue JSON endpoint (<c>/api/textures</c>) — browse/search/tags
///    and PBR-map support all work from it (the <c>files</c> array lists the maps).
///  • Full-resolution downloads (1K–8K) live on a token-protected BunnyCDN zone
///    (<c>cgbookcase-downloads.b-cdn.net</c> → 403 for any unsigned URL). The site mints a signed,
///    time-limited URL on the download page (the "wait a few seconds"); there is no public endpoint
///    that returns it, so a headless browser is needed to capture it.
///  • The open <c>/textures/thumbnails/&lt;Name&gt;_1K/…</c> path reliably serves real 1K maps; this
///    adapter currently resolves those. The proper gallery preview is the <c>renders/&lt;batch&gt;/…</c>
///    image, whose batch folder isn't in the API (needs a per-page scrape or the browser).
/// </summary>
public sealed class CgBookcaseProvider : IAssetProvider
{
    public const string ApiUrl = "https://www.cgbookcase.com/api/textures";
    public const string CdnBase = "https://cgbookcase.b-cdn.net/textures/thumbnails";

    private readonly HttpClient _http;
    private readonly TimeSpan _ttl = TimeSpan.FromHours(6);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _fetchedAt = DateTime.MinValue;
    private Dictionary<string, CgEntry> _byId = new();

    public CgBookcaseProvider(HttpClient http) => _http = http;

    public string Id => "cgbookcase";
    public string DisplayName => "cgbookcase";
    public IReadOnlyList<AssetKind> SupportedKinds { get; } = [AssetKind.Texture];

    public async Task<SearchPage> SearchAsync(AssetQuery query, CancellationToken ct = default)
    {
        if ((query.Kind ?? AssetKind.Texture) != AssetKind.Texture)
            return new SearchPage { Items = [], Page = query.Page, HasMore = false, Total = 0 };

        var all = (await GetAllAsync(ct).ConfigureAwait(false)).Values;
        IEnumerable<CgEntry> filtered = all;

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var terms = query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            filtered = filtered.Where(e => terms.All(e.Matches));
        }
        if (query.Categories is { Count: > 0 } cats)
            filtered = filtered.Where(e => e.Categories.Any(c => cats.Contains(c, StringComparer.OrdinalIgnoreCase)));

        var list = filtered.OrderByDescending(e => e.ReleaseDate).ToList();
        var pageItems = list.Skip(query.Page * query.PageSize).Take(query.PageSize).Select(ToSummary).ToList();
        return new SearchPage
        {
            Items = pageItems,
            Page = query.Page,
            HasMore = (query.Page + 1) * query.PageSize < list.Count,
            Total = list.Count,
        };
    }

    public async Task<AssetDetail> GetDetailAsync(string assetId, AssetKind kind, CancellationToken ct = default)
    {
        var entry = await FindAsync(assetId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"cgbookcase asset '{assetId}' not found.");
        var summary = ToSummary(entry);
        return new AssetDetail
        {
            Summary = summary,
            Description = null,
            Authors = ["cgbookcase"],
            Resolutions = ["1k"], // free tier; 2K–8K require a cgbookcase Patreon login
            Formats = ["png"],
            License = "CC0",
            SourceUrl = $"https://www.cgbookcase.com/textures/{entry.Slug}",
            MapSupport = summary.MapSupport,
        };
    }

    public async Task<IReadOnlyList<DownloadFile>> ResolveFilesAsync(
        string assetId, AssetKind kind, string resolution, FormatPrefs prefs, CancellationToken ct = default)
    {
        var entry = await FindAsync(assetId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"cgbookcase asset '{assetId}' not found.");

        var result = new List<DownloadFile>();
        var seen = new HashSet<MapType>();
        foreach (var file in entry.Files)
        {
            var map = MapClassifier.Classify(file);
            if (map is MapType.None or MapType.Arm || !seen.Add(map))
                continue;
            var mapUrlName = file.Replace("_", ""); // API "Base_Color" -> CDN "BaseColor"
            result.Add(new DownloadFile
            {
                Url = $"{CdnBase}/{entry.Name}_1K/{entry.Name}_1K_{mapUrlName}.png",
                FileName = $"{assetId}_{MapClassifier.Suffix(map)}.png",
                Role = DownloadRole.MapTexture,
                Map = map,
            });
        }
        return result;
    }

    // ---- catalogue ----

    private AssetSummary ToSummary(CgEntry e) => new()
    {
        ProviderId = Id,
        Id = e.Name,
        Name = e.Title,
        Kind = AssetKind.Texture,
        ThumbnailUrl = $"{CdnBase}/{e.Name}_1K/{e.Name}_1K_BaseColor.png?width=320",
        Tags = e.Tags,
        Categories = e.Categories,
        MapSupport = e.Files.Aggregate(MapType.None, (acc, f) => acc | MapClassifier.Classify(f)),
        MaxResolution = $"{e.MaxResolutionK}k",
    };

    private async Task<CgEntry?> FindAsync(string id, CancellationToken ct)
    {
        var all = await GetAllAsync(ct).ConfigureAwait(false);
        return all.GetValueOrDefault(id);
    }

    private async Task<Dictionary<string, CgEntry>> GetAllAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_byId.Count > 0 && DateTime.UtcNow - _fetchedAt < _ttl)
                return _byId;

            using var resp = await _http.GetAsync(ApiUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var map = new Dictionary<string, CgEntry>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var entry = CgEntry.Parse(el);
                map[entry.Name] = entry; // Name (title sans spaces) doubles as id + CDN key
            }
            _byId = map;
            _fetchedAt = DateTime.UtcNow;
            return _byId;
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record CgEntry(
        string Name, string Title, string Slug, IReadOnlyList<string> Files,
        IReadOnlyList<string> Tags, IReadOnlyList<string> Categories,
        IReadOnlyList<string> Queries, int MaxResolutionK, string ReleaseDate)
    {
        public bool Matches(string term) =>
            Title.Contains(term, StringComparison.OrdinalIgnoreCase)
            || Tags.Any(t => t.Contains(term, StringComparison.OrdinalIgnoreCase))
            || Categories.Any(c => c.Contains(term, StringComparison.OrdinalIgnoreCase))
            || Queries.Any(q => q.Contains(term, StringComparison.OrdinalIgnoreCase));

        public static CgEntry Parse(JsonElement el)
        {
            var title = el.GetProperty("title").GetString() ?? "";
            return new CgEntry(
                Name: new string(title.Where(c => !char.IsWhiteSpace(c)).ToArray()),
                Title: title,
                Slug: title.ToLowerInvariant().Replace(' ', '-'),
                Files: ReadArr(el, "files"),
                Tags: ReadArr(el, "tags"),
                Categories: ReadArr(el, "categories").Select(c => c.ToLowerInvariant()).ToList(),
                Queries: ReadArr(el, "queries"),
                MaxResolutionK: el.TryGetProperty("resolution", out var r) && r.TryGetInt32(out var v) ? v : 1,
                ReleaseDate: el.TryGetProperty("releasedate", out var d) ? d.GetString() ?? "" : "");
        }

        private static IReadOnlyList<string> ReadArr(JsonElement el, string prop) =>
            el.TryGetProperty(prop, out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList()
                : [];
    }
}
