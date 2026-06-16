using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using SboxAssetLib.Core.Model;

namespace SboxAssetLib.Core.Providers.AmbientCg;

/// <summary>
/// Adapter for the ambientCG API (https://ambientcg.com/api/v2/full_json). Materials are CC0
/// and ship as per-resolution zips containing the PBR maps, so resolution downloads are archives
/// the installer expands. Per-asset map support and thumbnails come free from the search response.
/// </summary>
public sealed partial class AmbientCgProvider : IAssetProvider
{
    public const string ApiUrl = "https://ambientcg.com/api/v2/full_json";

    private readonly HttpClient _http;

    public AmbientCgProvider(HttpClient http) => _http = http;

    public string Id => "ambientcg";
    public string DisplayName => "ambientCG";
    public IReadOnlyList<AssetKind> SupportedKinds { get; } = [AssetKind.Texture];

    public async Task<SearchPage> SearchAsync(AssetQuery query, CancellationToken ct = default)
    {
        if ((query.Kind ?? AssetKind.Texture) != AssetKind.Texture)
            return new SearchPage { Items = [], Page = query.Page, HasMore = false, Total = 0 };

        var qs = $"type=Material&limit={query.PageSize}&offset={query.Page * query.PageSize}"
                 + "&include=displayData,tagData,previewData,downloadData"
                 + (string.IsNullOrWhiteSpace(query.Text) ? "" : $"&q={Uri.EscapeDataString(query.Text)}");

        using var doc = await GetJsonAsync($"{ApiUrl}?{qs}", ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var total = root.TryGetProperty("numberOfResults", out var n) ? n.GetInt32() : 0;

        var items = new List<AssetSummary>();
        if (root.TryGetProperty("foundAssets", out var found) && found.ValueKind == JsonValueKind.Array)
            foreach (var a in found.EnumerateArray())
            {
                var summary = ToSummary(a);
                if (!query.PbrOnly || summary.IsPbr)
                    items.Add(summary);
            }

        return new SearchPage
        {
            Items = items,
            Page = query.Page,
            HasMore = (query.Page + 1) * query.PageSize < total,
            Total = total,
        };
    }

    public async Task<AssetDetail> GetDetailAsync(string assetId, AssetKind kind, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(
            $"{ApiUrl}?id={Uri.EscapeDataString(assetId)}&include=displayData,tagData,previewData,downloadData", ct)
            .ConfigureAwait(false);
        var assets = doc.RootElement.GetProperty("foundAssets");
        if (assets.GetArrayLength() == 0)
            throw new InvalidOperationException($"ambientCG asset '{assetId}' not found.");
        var a = assets[0];

        var (resolutions, formats) = CollectResolutionsAndFormats(a);
        var summary = ToSummary(a);
        return new AssetDetail
        {
            Summary = summary,
            Description = a.TryGetProperty("description", out var d) ? d.GetString() : null,
            Authors = ["ambientCG"],
            Resolutions = resolutions,
            Formats = formats,
            License = "CC0",
            SourceUrl = a.TryGetProperty("shortLink", out var sl) ? sl.GetString() : $"https://ambientcg.com/a/{assetId}",
            MapSupport = summary.MapSupport,
        };
    }

    public async Task<IReadOnlyList<DownloadFile>> ResolveFilesAsync(
        string assetId, AssetKind kind, string resolution, FormatPrefs prefs, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(
            $"{ApiUrl}?id={Uri.EscapeDataString(assetId)}&include=downloadData", ct).ConfigureAwait(false);
        var a = doc.RootElement.GetProperty("foundAssets")[0];

        // Build the list of (attribute -> download) for zip files, then pick res+format.
        var resUpper = resolution.ToUpperInvariant();
        var wanted = prefs.ImageFormats
            .Select(f => $"{resUpper}-{f.ToUpperInvariant()}")
            .ToList();

        foreach (var want in wanted)
            if (TryFindZip(a, want, out var url, out var size))
                return [new DownloadFile { Url = url, FileName = $"{assetId}_{resolution}.zip", Role = DownloadRole.Archive, Size = size }];

        // Fallback: any zip at the requested resolution.
        if (TryFindZipByResolution(a, resUpper, out var u2, out var s2))
            return [new DownloadFile { Url = u2, FileName = $"{assetId}_{resolution}.zip", Role = DownloadRole.Archive, Size = s2 }];

        return [];
    }

    // ---- mapping helpers --------------------------------------------------

    private AssetSummary ToSummary(JsonElement a)
    {
        var id = a.GetProperty("assetId").GetString()!;
        var (res, _) = CollectResolutionsAndFormats(a);
        return new AssetSummary
        {
            ProviderId = Id,
            Id = id,
            Name = a.TryGetProperty("displayName", out var dn) && !string.IsNullOrEmpty(dn.GetString()) ? dn.GetString()! : id,
            Kind = AssetKind.Texture,
            ThumbnailUrl = PickThumbnail(a),
            Tags = a.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList()
                : [],
            Categories = a.TryGetProperty("displayCategory", out var c) && c.ValueKind == JsonValueKind.String
                ? [c.GetString()!.ToLowerInvariant()]
                : [],
            MapSupport = ParseMapSupport(a),
            MaxResolution = res.LastOrDefault(),
        };
    }

    private static string? PickThumbnail(JsonElement a)
    {
        if (!a.TryGetProperty("previewImage", out var p) || p.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var key in new[] { "256-PNG", "128-PNG", "512-PNG", "64-PNG" })
            if (p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return p.EnumerateObject().FirstOrDefault().Value.GetString();
    }

    /// <summary>Derive PBR channels from the 3D-preview link's query params (cheap + accurate).</summary>
    private static MapType ParseMapSupport(JsonElement a)
    {
        if (!a.TryGetProperty("previewLinks", out var pl) || pl.ValueKind != JsonValueKind.Array || pl.GetArrayLength() == 0)
            return MapType.None;
        var url = pl[0].TryGetProperty("url", out var u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(url) || !url.Contains('#'))
            return MapType.None;

        var frag = url[(url.IndexOf('#') + 1)..];
        var q = HttpUtility.ParseQueryString(frag);
        var maps = MapType.None;
        if (q["color_url"] is not null) maps |= MapType.Albedo;
        if (q["normal_url"] is not null) maps |= MapType.Normal;
        if (q["roughness_url"] is not null) maps |= MapType.Roughness;
        if (q["metalness_url"] is not null) maps |= MapType.Metalness;
        if (q["ao_url"] is not null) maps |= MapType.AmbientOcclusion;
        if (q["displacement_url"] is not null) maps |= MapType.Height;
        return maps;
    }

    private static (IReadOnlyList<string> Resolutions, IReadOnlyList<string> Formats) CollectResolutionsAndFormats(JsonElement a)
    {
        var resSet = new SortedSet<int>();
        var fmtSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (attr, _, _) in EnumerateZips(a))
        {
            var m = AttrRegex().Match(attr);
            if (!m.Success)
                continue;
            resSet.Add(int.Parse(m.Groups[1].Value));
            fmtSet.Add(m.Groups[2].Value.ToLowerInvariant());
        }
        return (resSet.Select(r => $"{r}k").ToList(), fmtSet.ToList());
    }

    private static bool TryFindZip(JsonElement a, string attribute, out string url, out long size)
    {
        foreach (var (attr, u, s) in EnumerateZips(a))
            if (string.Equals(attr, attribute, StringComparison.OrdinalIgnoreCase))
            {
                url = u;
                size = s;
                return true;
            }
        url = "";
        size = 0;
        return false;
    }

    private static bool TryFindZipByResolution(JsonElement a, string resUpper, out string url, out long size)
    {
        foreach (var (attr, u, s) in EnumerateZips(a))
            if (attr.StartsWith(resUpper + "-", StringComparison.OrdinalIgnoreCase) && AttrRegex().IsMatch(attr))
            {
                url = u;
                size = s;
                return true;
            }
        url = "";
        size = 0;
        return false;
    }

    private static IEnumerable<(string Attribute, string Url, long Size)> EnumerateZips(JsonElement a)
    {
        if (!a.TryGetProperty("downloadFolders", out var folders) || folders.ValueKind != JsonValueKind.Object)
            yield break;
        foreach (var folder in folders.EnumerateObject())
        {
            if (!folder.Value.TryGetProperty("downloadFiletypeCategories", out var cats) || cats.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var cat in cats.EnumerateObject())
            {
                if (!cat.Value.TryGetProperty("downloads", out var dls) || dls.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var dl in dls.EnumerateArray())
                {
                    if (!string.Equals(dl.TryGetProperty("filetype", out var ft) ? ft.GetString() : null, "zip", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var attr = dl.TryGetProperty("attribute", out var at) ? at.GetString() ?? "" : "";
                    var url = dl.TryGetProperty("downloadLink", out var ul) ? ul.GetString() ?? "" : "";
                    var size = dl.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var v) ? v : 0;
                    if (attr.Length > 0 && url.Length > 0)
                        yield return (attr, url, size);
                }
            }
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    [GeneratedRegex(@"^(\d+)K-(JPG|PNG)$", RegexOptions.IgnoreCase)]
    private static partial Regex AttrRegex();
}
