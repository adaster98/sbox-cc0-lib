using System.Net;
using System.Text.RegularExpressions;
using SboxAssetLib.Core.Model;

namespace SboxAssetLib.Core.Providers.TextureCan;

/// <summary>
/// Adapter for texturecan.com. Listing and detail pages are static HTML; downloads are direct
/// zip links for texture map packages and model archives.
/// </summary>
public sealed partial class TextureCanProvider : IAssetProvider
{
    public const string BaseUrl = "https://www.texturecan.com";

    // TextureCan texture packs are always PBR but don't expose a crawlable file list, so we claim the
    // channels every pack ships (which also satisfies MapType.IsPbr). The importer reclassifies the
    // real maps from the downloaded archive on install — this is only for the gallery badges.
    private const MapType TexturePbrMaps = MapType.Albedo | MapType.Normal | MapType.Roughness;

    private readonly HttpClient _http;
    private readonly TimeSpan _ttl = TimeSpan.FromHours(6);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<AssetKind, (DateTime At, IReadOnlyList<Entry> Items)> _listCache = new();
    private readonly Dictionary<string, DetailData> _detailCache = new(StringComparer.OrdinalIgnoreCase);

    public TextureCanProvider(HttpClient http) => _http = http;

    public string Id => "texturecan";
    public string DisplayName => "TextureCan";
    public IReadOnlyList<AssetKind> SupportedKinds { get; } = [AssetKind.Texture, AssetKind.Model];

    public async Task<SearchPage> SearchAsync(AssetQuery query, CancellationToken ct = default)
    {
        var kind = query.Kind ?? AssetKind.Texture;
        if (!SupportedKinds.Contains(kind))
            return new SearchPage { Items = [], Page = query.Page, HasMore = false, Total = 0 };

        var all = await GetAllAsync(kind, ct).ConfigureAwait(false);
        IEnumerable<Entry> filtered = all;

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var terms = query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            filtered = filtered.Where(e => terms.All(e.Matches));
        }

        if (query.Categories is { Count: > 0 } cats)
            filtered = filtered.Where(e => e.Categories.Any(c => cats.Contains(c, StringComparer.OrdinalIgnoreCase)));

        var list = filtered.ToList();
        var pageItems = list.Skip(query.Page * query.PageSize).Take(query.PageSize).Select(e => ToSummary(e)).ToList();
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
        var entry = await FindAsync(assetId, kind, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"TextureCan asset '{assetId}' not found.");
        var detail = await GetDetailDataAsync(entry, ct).ConfigureAwait(false);
        return new AssetDetail
        {
            Summary = ToSummary(entry, detail),
            Description = detail.Description,
            Authors = ["TextureCan"],
            Resolutions = detail.Resolutions,
            Formats = detail.Formats,
            License = "CC0",
            SourceUrl = BaseUrl + entry.DetailPath,
            MapSupport = MapsFor(entry.Kind),
        };
    }

    public Task<MapType> GetMapSupportAsync(string assetId, AssetKind kind, CancellationToken ct = default) =>
        Task.FromResult(MapsFor(kind));

    private static MapType MapsFor(AssetKind kind) =>
        kind == AssetKind.Texture ? TexturePbrMaps : MapType.None;

    public async Task<IReadOnlyList<DownloadFile>> ResolveFilesAsync(
        string assetId, AssetKind kind, string resolution, FormatPrefs prefs, CancellationToken ct = default)
    {
        var entry = await FindAsync(assetId, kind, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"TextureCan asset '{assetId}' not found.");
        var detail = await GetDetailDataAsync(entry, ct).ConfigureAwait(false);

        if (kind == AssetKind.Model)
        {
            var modelUrl = detail.Downloads.Values.FirstOrDefault()
                           ?? throw new InvalidOperationException($"TextureCan model '{assetId}' has no download link.");
            return
            [
                new DownloadFile
                {
                    Url = modelUrl,
                    FileName = $"{assetId}.zip",
                    Role = DownloadRole.Archive,
                },
            ];
        }

        var key = resolution.ToLowerInvariant();
        if (!detail.Downloads.TryGetValue(key, out var url))
            url = detail.Downloads.Values.LastOrDefault()
                  ?? throw new InvalidOperationException($"TextureCan texture '{assetId}' has no download link.");

        return
        [
            new DownloadFile
            {
                Url = url,
                FileName = $"{assetId}_{key}.zip",
                Role = DownloadRole.Archive,
            },
        ];
    }

    private AssetSummary ToSummary(Entry e, DetailData? detail = null) => new()
    {
        ProviderId = Id,
        Id = e.Id,
        Name = detail?.Name ?? e.Name,
        Kind = e.Kind,
        ThumbnailUrl = detail?.ThumbnailUrl ?? e.ThumbnailUrl,
        Tags = detail?.Tags ?? e.Tags,
        Categories = detail?.Categories ?? e.Categories,
        MapSupport = MapsFor(e.Kind),
        MaxResolution = detail?.Resolutions.LastOrDefault() ?? (e.Kind == AssetKind.Model ? "4k" : "4k"),
    };

    private async Task<Entry?> FindAsync(string id, AssetKind kind, CancellationToken ct)
    {
        var all = await GetAllAsync(kind, ct).ConfigureAwait(false);
        return all.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<Entry>> GetAllAsync(AssetKind kind, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_listCache.TryGetValue(kind, out var cached) && DateTime.UtcNow - cached.At < _ttl)
                return cached.Items;

            var first = await GetStringAsync(ListUrl(kind, 1), ct).ConfigureAwait(false);
            var pages = Math.Max(1, MaxPage(first));
            var entries = ReadEntries(first, kind).ToList();

            for (var page = 2; page <= pages; page++)
            {
                var html = await GetStringAsync(ListUrl(kind, page), ct).ConfigureAwait(false);
                entries.AddRange(ReadEntries(html, kind));
            }

            var distinct = entries
                .GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            _listCache[kind] = (DateTime.UtcNow, distinct);
            return distinct;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<DetailData> GetDetailDataAsync(Entry entry, CancellationToken ct)
    {
        if (_detailCache.TryGetValue(entry.Id, out var cached))
            return cached;

        var html = await GetStringAsync(BaseUrl + entry.DetailPath, ct).ConfigureAwait(false);
        var downloads = ReadDownloads(html, entry.Kind);
        var resolutions = entry.Kind == AssetKind.Model
            ? new[] { "4k" }
            : downloads.Keys.Where(k => k.EndsWith('k')).OrderBy(ResRank).ToArray();

        var detail = new DetailData(
            Name: MetaContent(html, "tex1:name") ?? entry.Name,
            Description: MetaContent(html, "description") ?? entry.Description,
            ThumbnailUrl: MetaContent(html, "tex1:preview-image") ?? entry.ThumbnailUrl,
            Tags: SplitCsv(MetaContent(html, "tex1:tags")),
            Categories: ReadCategories(html, entry.Kind, entry.Categories),
            Resolutions: resolutions.Length > 0 ? resolutions : ["4k"],
            Formats: entry.Kind == AssetKind.Model ? ["fbx", "gltf", "blend"] : ["zip"],
            Downloads: downloads);

        _detailCache[entry.Id] = detail;
        return detail;
    }

    private static string ListUrl(AssetKind kind, int page) => kind == AssetKind.Model
        ? page <= 1 ? BaseUrl + "/models/" : $"{BaseUrl}/models/category/New/{page}/"
        : page <= 1 ? BaseUrl + "/category/New/" : $"{BaseUrl}/category/New/{page}/";

    private static IEnumerable<Entry> ReadEntries(string html, AssetKind kind)
    {
        foreach (Match match in ArticleRegex().Matches(html))
        {
            var href = match.Groups["href"].Value;
            if (kind == AssetKind.Texture && !href.StartsWith("/details/", StringComparison.Ordinal))
                continue;
            if (kind == AssetKind.Model && !href.StartsWith("/models/details/", StringComparison.Ordinal))
                continue;

            var number = DetailIdRegex().Match(href).Groups["id"].Value;
            var title = Clean(match.Groups["title"].Value);
            var desc = Clean(match.Groups["desc"].Value);
            var categories = new[] { CategoryFromTitle(title) };
            yield return new Entry(
                Id: kind == AssetKind.Model ? $"model-{number}" : $"texture-{number}",
                Kind: kind,
                Name: title,
                Description: desc,
                DetailPath: href,
                ThumbnailUrl: Absolute(match.Groups["thumb"].Value),
                Tags: [],
                Categories: categories.Where(c => c.Length > 0).ToList());
        }
    }

    private static int MaxPage(string html)
    {
        var max = 1;
        foreach (Match match in PageRegex().Matches(html))
            if (int.TryParse(match.Groups["page"].Value, out var page) && page > max)
                max = page;
        return max;
    }

    private static Dictionary<string, string> ReadDownloads(string html, AssetKind kind)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in DownloadRegex().Matches(html))
        {
            var href = Absolute(match.Groups["href"].Value);
            var label = Clean(match.Groups["label"].Value);
            if (kind == AssetKind.Model)
            {
                result["4k"] = href;
                continue;
            }

            var res = ResolutionLabelRegex().Match(label);
            if (res.Success)
                result[res.Groups["res"].Value.ToLowerInvariant() + "k"] = href;
        }
        return result;
    }

    private static IReadOnlyList<string> ReadCategories(string html, AssetKind kind, IReadOnlyList<string> fallback)
    {
        var regex = kind == AssetKind.Model ? ModelCategoryRegex() : TextureCategoryRegex();
        var match = regex.Match(html);
        return match.Success ? [Clean(match.Groups["cat"].Value)] : fallback;
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static string? MetaContent(string html, string name)
    {
        var escaped = Regex.Escape(name);
        var match = Regex.Match(
            html,
            $"""<meta\s+(?:name|property)="{escaped}"\s+content="(?<content>[^"]*)"\s*/?>""",
            RegexOptions.IgnoreCase);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["content"].Value) : null;
    }

    private static string CategoryFromTitle(string title)
    {
        var match = CategoryInTitleRegex().Match(title);
        return match.Success ? match.Groups["cat"].Value : "";
    }

    private static IReadOnlyList<string> SplitCsv(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    private static string Clean(string html)
    {
        var withSpaces = BrRegex().Replace(html, " ");
        var noTags = TagRegex().Replace(withSpaces, " ");
        var decoded = WebUtility.HtmlDecode(noTags);
        return WhitespaceRegex().Replace(decoded, " ").Trim();
    }

    private static string Absolute(string href) =>
        href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : BaseUrl + href;

    private static int ResRank(string res) =>
        int.TryParse(res.TrimEnd('k', 'K'), out var n) ? n : 0;

    [GeneratedRegex("""<div class="article-box">\s*<div class="texture-header"><a href="(?<href>[^"]+)">(?<title>.*?)</a></div>\s*<div class="texture-img"><a href="[^"]+"><img src="(?<thumb>[^"]+)"[^>]*></a></div>\s*<div class="texture-desc">(?<desc>.*?)</div>""", RegexOptions.Singleline)]
    private static partial Regex ArticleRegex();

    [GeneratedRegex("""/details/(?<id>\d+)/""")]
    private static partial Regex DetailIdRegex();

    [GeneratedRegex("""<a href="[^"]*/(?<page>\d+)/"><div class="pageNumber">""")]
    private static partial Regex PageRegex();

    [GeneratedRegex("""<a href="(?<href>/downloads/[^"]+\.zip)"[^>]*class="download"[^>]*>(?<label>.*?)</a>""", RegexOptions.Singleline)]
    private static partial Regex DownloadRegex();

    [GeneratedRegex("""(?<res>\d+)K\s+Maps""", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionLabelRegex();

    [GeneratedRegex("""<a href="/category/[^"]+/".*?>(?<cat>[^<]+)</a>""", RegexOptions.Singleline)]
    private static partial Regex TextureCategoryRegex();

    [GeneratedRegex("""<a href="/models/category/[^"]+/".*?>(?<cat>[^<]+)</a>""", RegexOptions.Singleline)]
    private static partial Regex ModelCategoryRegex();

    [GeneratedRegex(@"\((?<cat>[A-Za-z]+)[^)]*\)$")]
    private static partial Regex CategoryInTitleRegex();

    [GeneratedRegex("""<br\s*/?>""", RegexOptions.IgnoreCase)]
    private static partial Regex BrRegex();

    [GeneratedRegex("""<[^>]+>""")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private sealed record Entry(
        string Id, AssetKind Kind, string Name, string Description, string DetailPath,
        string ThumbnailUrl, IReadOnlyList<string> Tags, IReadOnlyList<string> Categories)
    {
        public bool Matches(string term) =>
            Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || Description.Contains(term, StringComparison.OrdinalIgnoreCase)
            || Tags.Any(t => t.Contains(term, StringComparison.OrdinalIgnoreCase))
            || Categories.Any(c => c.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record DetailData(
        string Name, string Description, string ThumbnailUrl, IReadOnlyList<string> Tags,
        IReadOnlyList<string> Categories, IReadOnlyList<string> Resolutions,
        IReadOnlyList<string> Formats, IReadOnlyDictionary<string, string> Downloads);
}
