using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using SboxAssetLib.Core.Model;

namespace SboxAssetLib.Core.Providers.Cc0Textures;

/// <summary>
/// Adapter for cc0-textures.com. The catalogue is emitted as static Next.js JSON and each
/// texture resolves to a direct 4K zip on download.cc0-textures.com.
/// </summary>
public sealed partial class Cc0TexturesProvider : IAssetProvider
{
    public const string BaseUrl = "https://cc0-textures.com";
    public const string DownloadBaseUrl = "https://download.cc0-textures.com";

    private readonly HttpClient _http;
    private readonly TimeSpan _ttl = TimeSpan.FromHours(6);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _fetchedAt = DateTime.MinValue;
    private List<Entry> _entries = [];
    private Dictionary<string, Entry> _byId = new(StringComparer.OrdinalIgnoreCase);

    public Cc0TexturesProvider(HttpClient http) => _http = http;

    public string Id => "cc0textures";
    public string DisplayName => "CC0 Textures";
    public IReadOnlyList<AssetKind> SupportedKinds { get; } = [AssetKind.Texture];

    public async Task<SearchPage> SearchAsync(AssetQuery query, CancellationToken ct = default)
    {
        if ((query.Kind ?? AssetKind.Texture) != AssetKind.Texture)
            return new SearchPage { Items = [], Page = query.Page, HasMore = false, Total = 0 };

        var all = await GetAllAsync(ct).ConfigureAwait(false);
        IEnumerable<Entry> filtered = all;

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var terms = query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            filtered = filtered.Where(e => terms.All(e.Matches));
        }

        if (query.Categories is { Count: > 0 } cats)
            filtered = filtered.Where(e => e.Tags.Any(t => cats.Contains(t, StringComparer.OrdinalIgnoreCase)));

        var list = filtered.ToList();
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
                    ?? throw new InvalidOperationException($"cc0-textures asset '{assetId}' not found.");
        var summary = ToSummary(entry);
        return new AssetDetail
        {
            Summary = summary,
            Description = entry.Description,
            Authors = [entry.VendorTitle],
            Resolutions = ["4k"],
            Formats = ["zip"],
            License = "CC0",
            SourceUrl = $"{BaseUrl}/t/{entry.Slug}",
            MapSupport = MapType.None,
        };
    }

    public Task<MapType> GetMapSupportAsync(string assetId, AssetKind kind, CancellationToken ct = default) =>
        Task.FromResult(MapType.None);

    public async Task<IReadOnlyList<DownloadFile>> ResolveFilesAsync(
        string assetId, AssetKind kind, string resolution, FormatPrefs prefs, CancellationToken ct = default)
    {
        var entry = await FindAsync(assetId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"cc0-textures asset '{assetId}' not found.");
        return
        [
            new DownloadFile
            {
                Url = $"{DownloadBaseUrl}/{entry.Vendor}/{entry.Download}",
                FileName = $"{entry.Id}_4k.zip",
                Role = DownloadRole.Archive,
            },
        ];
    }

    private AssetSummary ToSummary(Entry e) => new()
    {
        ProviderId = Id,
        Id = e.Id,
        Name = e.Title,
        Kind = AssetKind.Texture,
        ThumbnailUrl = $"{BaseUrl}/thumbs/{e.Vendor}/{e.Thumb}",
        Tags = e.Tags,
        Categories = e.Tags,
        MapSupport = MapType.None,
        MaxResolution = "4k",
    };

    private async Task<Entry?> FindAsync(string id, CancellationToken ct)
    {
        await GetAllAsync(ct).ConfigureAwait(false);
        return _byId.GetValueOrDefault(id);
    }

    private async Task<IReadOnlyList<Entry>> GetAllAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_entries.Count > 0 && DateTime.UtcNow - _fetchedAt < _ttl)
                return _entries;

            var home = await GetStringAsync(BaseUrl + "/", ct).ConfigureAwait(false);
            using var data = JsonDocument.Parse(ExtractNextData(home));
            var buildId = data.RootElement.GetProperty("buildId").GetString()
                          ?? throw new InvalidDataException("cc0-textures build id missing.");
            var pageProps = data.RootElement.GetProperty("props").GetProperty("pageProps");
            var total = pageProps.TryGetProperty("totalInCategory", out var t) ? t.GetInt32() : 0;
            var result = ReadEntries(pageProps.GetProperty("textures")).ToList();

            var pages = Math.Max(1, (int)Math.Ceiling(total / 60d));
            for (var page = 2; page <= pages; page++)
            {
                using var doc = await GetJsonAsync($"{BaseUrl}/_next/data/{buildId}/{page}.json", ct).ConfigureAwait(false);
                if (doc.RootElement.TryGetProperty("pageProps", out var props)
                    && props.TryGetProperty("textures", out var textures))
                    result.AddRange(ReadEntries(textures));
            }

            _entries = result;
            _byId = result.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
            _fetchedAt = DateTime.UtcNow;
            return _entries;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string ExtractNextData(string html)
    {
        var match = NextDataRegex().Match(html);
        if (!match.Success)
            throw new InvalidDataException("cc0-textures Next data script not found.");
        return WebUtility.HtmlDecode(match.Groups["json"].Value);
    }

    private static IEnumerable<Entry> ReadEntries(JsonElement textures)
    {
        foreach (var el in textures.EnumerateArray())
        {
            var slug = GetString(el, "slug");
            if (string.IsNullOrWhiteSpace(slug))
                continue;
            yield return new Entry(
                Id: slug,
                Title: GetString(el, "title"),
                Description: GetString(el, "desc"),
                Slug: slug,
                Tags: ReadStringArray(el, "tags"),
                Download: GetString(el, "download"),
                Thumb: GetString(el, "thumb"),
                Vendor: GetString(el, "vendor"),
                VendorTitle: GetString(el, "vendor_title"));
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static string GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static IReadOnlyList<string> ReadStringArray(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList()
            : [];

    [GeneratedRegex("""<script id="__NEXT_DATA__" type="application/json">(?<json>.*?)</script>""", RegexOptions.Singleline)]
    private static partial Regex NextDataRegex();

    private sealed record Entry(
        string Id, string Title, string Description, string Slug, IReadOnlyList<string> Tags,
        string Download, string Thumb, string Vendor, string VendorTitle)
    {
        public bool Matches(string term) =>
            Title.Contains(term, StringComparison.OrdinalIgnoreCase)
            || Description.Contains(term, StringComparison.OrdinalIgnoreCase)
            || Slug.Contains(term, StringComparison.OrdinalIgnoreCase)
            || Tags.Any(t => t.Contains(term, StringComparison.OrdinalIgnoreCase))
            || VendorTitle.Contains(term, StringComparison.OrdinalIgnoreCase);
    }
}
