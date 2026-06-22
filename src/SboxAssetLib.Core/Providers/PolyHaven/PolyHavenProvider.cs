using System.Text.Json;
using SboxAssetLib.Core.Model;

namespace SboxAssetLib.Core.Providers.PolyHaven;

/// <summary>
/// Adapter for the Poly Haven public API (https://api.polyhaven.com).
///
/// The API has no server-side text search or paging: <c>/assets?type=…</c> returns the
/// entire catalogue for a type as one JSON object keyed by id. We cache that list and
/// filter/page client-side. Exact per-asset map support lives in <c>/files/{id}</c>,
/// which we fetch lazily for the detail panel.
///
/// NOTE: the API requires a unique User-Agent header or it returns 403.
/// </summary>
public sealed class PolyHavenProvider : IAssetProvider
{
    public const string BaseUrl = "https://api.polyhaven.com";

    private readonly HttpClient _http;
    private readonly TimeSpan _listTtl = TimeSpan.FromHours(6);
    private readonly Dictionary<AssetKind, (DateTime At, IReadOnlyList<AssetSummary> Items)> _listCache = new();
    private readonly Dictionary<string, JsonElement> _filesCache = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PolyHavenProvider(HttpClient http)
    {
        _http = http;
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("sbox-asset-lib/0.1 (+https://github.com/)");
    }

    public string Id => "polyhaven";
    public string DisplayName => "Poly Haven";
    public IReadOnlyList<AssetKind> SupportedKinds { get; } = [AssetKind.Texture, AssetKind.Model, AssetKind.Hdri];

    // ---- Search -----------------------------------------------------------

    public async Task<SearchPage> SearchAsync(AssetQuery query, CancellationToken ct = default)
    {
        var kind = query.Kind ?? AssetKind.Texture;
        var all = await GetListAsync(kind, ct).ConfigureAwait(false);

        IEnumerable<AssetSummary> filtered = all;

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var terms = query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            filtered = filtered.Where(a => terms.All(t => Matches(a, t)));
        }

        if (query.Categories is { Count: > 0 } cats)
            filtered = filtered.Where(a => a.Categories.Any(c => cats.Contains(c, StringComparer.OrdinalIgnoreCase)));

        if (query.Tags is { Count: > 0 } tags)
            filtered = filtered.Where(a => a.Tags.Any(tg => tags.Contains(tg, StringComparer.OrdinalIgnoreCase)));

        // PbrOnly is a no-op for Poly Haven textures/models (always PBR); HDRIs are excluded.
        if (query.PbrOnly)
            filtered = filtered.Where(a => a.Kind != AssetKind.Hdri);

        var list = filtered.ToList();
        var pageItems = list.Skip(query.Page * query.PageSize).Take(query.PageSize).ToList();
        return new SearchPage
        {
            Items = pageItems,
            Page = query.Page,
            HasMore = (query.Page + 1) * query.PageSize < list.Count,
            Total = list.Count,
        };
    }

    private static bool Matches(AssetSummary a, string term) =>
        a.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
        || a.Id.Contains(term, StringComparison.OrdinalIgnoreCase)
        || a.Tags.Any(t => t.Contains(term, StringComparison.OrdinalIgnoreCase))
        || a.Categories.Any(c => c.Contains(term, StringComparison.OrdinalIgnoreCase));

    private async Task<IReadOnlyList<AssetSummary>> GetListAsync(AssetKind kind, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_listCache.TryGetValue(kind, out var c) && DateTime.UtcNow - c.At < _listTtl)
                return c.Items;

            using var doc = await GetJsonAsync($"{BaseUrl}/assets?type={TypeParam(kind)}", ct).ConfigureAwait(false);
            var items = new List<AssetSummary>(doc.RootElement.GetPropertyCount());
            foreach (var prop in doc.RootElement.EnumerateObject())
                items.Add(ToSummary(prop.Name, prop.Value, kind));

            _listCache[kind] = (DateTime.UtcNow, items);
            return items;
        }
        finally
        {
            _gate.Release();
        }
    }

    private AssetSummary ToSummary(string id, JsonElement e, AssetKind kind) => new()
    {
        ProviderId = Id,
        Id = id,
        Name = e.TryGetProperty("name", out var n) ? n.GetString() ?? id : id,
        Kind = kind,
        ThumbnailUrl = e.TryGetProperty("thumbnail_url", out var t) ? t.GetString() : null,
        Tags = ReadStringArray(e, "tags"),
        Categories = ReadStringArray(e, "categories"),
        MaxResolution = e.TryGetProperty("max_resolution", out var mr) && mr.ValueKind == JsonValueKind.Array && mr.GetArrayLength() > 0
            ? ResLabel(mr[0].GetInt32())
            : null,
        MapSupport = MapType.None, // filled in by GetDetailAsync (needs /files)
    };

    // ---- Detail -----------------------------------------------------------

    public async Task<AssetDetail> GetDetailAsync(string assetId, AssetKind kind, CancellationToken ct = default)
    {
        var files = await GetFilesAsync(assetId, ct).ConfigureAwait(false);
        using var info = await GetJsonAsync($"{BaseUrl}/info/{assetId}", ct).ConfigureAwait(false);
        var root = info.RootElement;

        var summary = new AssetSummary
        {
            ProviderId = Id,
            Id = assetId,
            Name = root.TryGetProperty("name", out var n) ? n.GetString() ?? assetId : assetId,
            Kind = kind,
            ThumbnailUrl = root.TryGetProperty("thumbnail_url", out var t) ? t.GetString() : null,
            Tags = ReadStringArray(root, "tags"),
            Categories = ReadStringArray(root, "categories"),
            MaxResolution = root.TryGetProperty("max_resolution", out var mr) && mr.ValueKind == JsonValueKind.Array && mr.GetArrayLength() > 0
                ? ResLabel(mr[0].GetInt32())
                : null,
            MapSupport = ComputeMapSupport(files, kind),
        };

        return new AssetDetail
        {
            Summary = summary,
            Description = root.TryGetProperty("description", out var d) ? d.GetString() : null,
            Authors = root.TryGetProperty("authors", out var au) && au.ValueKind == JsonValueKind.Object
                ? au.EnumerateObject().Select(p => p.Name).ToList()
                : [],
            Resolutions = CollectResolutions(files, kind),
            Formats = CollectFormats(files, kind),
            License = "CC0",
            SourceUrl = $"https://polyhaven.com/a/{assetId}",
            MapSupport = summary.MapSupport,
        };
    }

    public async Task<MapType> GetMapSupportAsync(string assetId, AssetKind kind, CancellationToken ct = default)
    {
        if (kind == AssetKind.Model)
            return MapType.None;
        var files = await GetFilesAsync(assetId, ct).ConfigureAwait(false);
        return ComputeMapSupport(files, kind);
    }

    // ---- Resolve files ----------------------------------------------------

    public async Task<IReadOnlyList<DownloadFile>> ResolveFilesAsync(
        string assetId, AssetKind kind, string resolution, FormatPrefs prefs, CancellationToken ct = default)
    {
        var files = await GetFilesAsync(assetId, ct).ConfigureAwait(false);
        return kind == AssetKind.Model
            ? ResolveModel(assetId, files, resolution, prefs)
            : ResolveTexture(assetId, files, resolution, prefs);
    }

    private List<DownloadFile> ResolveTexture(string id, JsonElement files, string res, FormatPrefs prefs)
    {
        // Choose the normal variant up-front so we don't emit both gl and dx.
        string? normalKey = PickNormalKey(files, prefs.Normal);

        // Pick a single source key per channel. Providers often ship both a standalone map
        // and a packed convenience map that classify to the same channel (e.g. "Rough" and
        // "rough_ao"); prefer the cleanest (shortest) key, and drop packed ARM entirely since
        // complex.shader consumes separate channels.
        var chosen = new Dictionary<MapType, string>();
        foreach (var prop in files.EnumerateObject())
        {
            var map = ClassifyTextureKey(prop.Name);
            if (map is MapType.None or MapType.Arm)
                continue;
            if (map == MapType.Normal)
            {
                if (string.Equals(prop.Name, normalKey, StringComparison.OrdinalIgnoreCase))
                    chosen[map] = prop.Name;
                continue;
            }
            if (!chosen.TryGetValue(map, out var existing) || prop.Name.Length < existing.Length)
                chosen[map] = prop.Name;
        }

        var result = new List<DownloadFile>();
        foreach (var (map, key) in chosen)
        {
            var node = files.GetProperty(key);
            if (!TryPickResolution(node, res, out var resNode))
                continue;
            if (!TryPickFormat(resNode, prefs.ImageFormats, out var ext, out var leaf))
                continue;
            result.Add(LeafToFile(leaf, $"{id}_{Suffix(map)}.{ext}", DownloadRole.MapTexture, map));
        }

        return result;
    }

    private List<DownloadFile> ResolveModel(string id, JsonElement files, string res, FormatPrefs prefs)
    {
        var result = new List<DownloadFile>();
        // Top-level keys for models are formats (fbx/gltf/glb/blend/usd).
        string? fmt = prefs.ModelFormats.FirstOrDefault(f => files.TryGetProperty(f, out _));
        if (fmt is null || !files.TryGetProperty(fmt, out var fmtNode))
            return result;
        if (!TryPickResolution(fmtNode, res, out var resNode))
            return result;
        if (!resNode.TryGetProperty(fmt, out var leaf))
        {
            // Some trees nest as { res: { fmt: leaf } }, others as { res: leaf }.
            leaf = resNode;
        }

        var meshName = $"{id}.{fmt}";
        result.Add(LeafToFile(leaf, meshName, DownloadRole.Mesh, MapType.None));

        // Pull in dependency files (textures referenced by the mesh) preserving their relative paths.
        if (leaf.TryGetProperty("include", out var inc) && inc.ValueKind == JsonValueKind.Object)
        {
            foreach (var dep in inc.EnumerateObject())
                result.Add(LeafToFile(dep.Value, dep.Name, DownloadRole.Dependency, MapType.None));
        }

        // FBX drops Blender's material-node UV transforms. Keep the tiny glTF document as
        // metadata so the importer can recover per-material texture tiling without using its mesh.
        if (fmt is not ("gltf" or "glb")
            && TryGetModelLeaf(files, "gltf", res, out var metadataLeaf))
        {
            result.Add(LeafToFile(
                metadataLeaf, $"{id}.material-metadata.gltf", DownloadRole.ModelMetadata, MapType.None));
        }

        return result;
    }

    private static bool TryGetModelLeaf(JsonElement files, string format, string resolution, out JsonElement leaf)
    {
        leaf = default;
        if (!files.TryGetProperty(format, out var formatNode)
            || !TryPickResolution(formatNode, resolution, out var resolutionNode))
            return false;
        leaf = resolutionNode.TryGetProperty(format, out var nested) ? nested : resolutionNode;
        return leaf.ValueKind == JsonValueKind.Object && leaf.TryGetProperty("url", out _);
    }

    // ---- /files helpers ---------------------------------------------------

    private async Task<JsonElement> GetFilesAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_filesCache.TryGetValue(id, out var cached))
                return cached;
            // Keep the document alive for the provider's lifetime (clone into a detached element).
            using var doc = await GetJsonAsync($"{BaseUrl}/files/{id}", ct).ConfigureAwait(false);
            var clone = doc.RootElement.Clone();
            _filesCache[id] = clone;
            return clone;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static MapType ComputeMapSupport(JsonElement files, AssetKind kind)
    {
        if (kind == AssetKind.Model)
            return MapType.None;
        var maps = MapType.None;
        foreach (var prop in files.EnumerateObject())
            maps |= ClassifyTextureKey(prop.Name);
        return maps;
    }

    private static IReadOnlyList<string> CollectResolutions(JsonElement files, AssetKind kind)
    {
        var set = new SortedSet<string>(ResComparer.Instance);
        foreach (var top in files.EnumerateObject())
        {
            if (kind != AssetKind.Model && ClassifyTextureKey(top.Name) == MapType.None)
                continue;
            if (top.Value.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var res in top.Value.EnumerateObject())
                set.Add(res.Name);
        }
        return set.ToList();
    }

    private static IReadOnlyList<string> CollectFormats(JsonElement files, AssetKind kind)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (kind == AssetKind.Model)
        {
            foreach (var top in files.EnumerateObject())
                if (top.Name is "fbx" or "gltf" or "glb" or "blend" or "usd")
                    set.Add(top.Name);
            return set.ToList();
        }
        // For textures, report the image formats available under any map's first resolution.
        foreach (var top in files.EnumerateObject())
        {
            if (ClassifyTextureKey(top.Name) == MapType.None || top.Value.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var res in top.Value.EnumerateObject())
            {
                if (res.Value.ValueKind == JsonValueKind.Object)
                    foreach (var f in res.Value.EnumerateObject())
                        set.Add(f.Name);
                break;
            }
        }
        return set.ToList();
    }

    private static string? PickNormalKey(JsonElement files, NormalConvention conv)
    {
        bool gl = files.TryGetProperty("nor_gl", out _);
        bool dx = files.TryGetProperty("nor_dx", out _);
        if (conv == NormalConvention.OpenGl)
            return gl ? "nor_gl" : dx ? "nor_dx" : null;
        return dx ? "nor_dx" : gl ? "nor_gl" : null;
    }

    private static bool TryPickResolution(JsonElement mapNode, string wanted, out JsonElement resNode)
    {
        resNode = default;
        if (mapNode.ValueKind != JsonValueKind.Object)
            return false;
        if (mapNode.TryGetProperty(wanted, out resNode))
            return true;
        // Fall back to the highest available resolution <= wanted, else the largest overall.
        var keys = mapNode.EnumerateObject().Select(p => p.Name).ToList();
        if (keys.Count == 0)
            return false;
        keys.Sort(ResComparer.Instance);
        int wantedK = ResRank(wanted);
        var best = keys.LastOrDefault(k => ResRank(k) <= wantedK) ?? keys[^1];
        return mapNode.TryGetProperty(best, out resNode);
    }

    private static bool TryPickFormat(JsonElement resNode, IReadOnlyList<string> prefs, out string ext, out JsonElement leaf)
    {
        foreach (var p in prefs)
            if (resNode.TryGetProperty(p, out leaf))
            {
                ext = p;
                return true;
            }
        // Otherwise take the first format present.
        foreach (var f in resNode.EnumerateObject())
            if (f.Value.ValueKind == JsonValueKind.Object && f.Value.TryGetProperty("url", out _))
            {
                ext = f.Name;
                leaf = f.Value;
                return true;
            }
        ext = "";
        leaf = default;
        return false;
    }

    private static DownloadFile LeafToFile(JsonElement leaf, string fileName, DownloadRole role, MapType map) => new()
    {
        Url = leaf.GetProperty("url").GetString()!,
        FileName = fileName,
        Role = role,
        Map = map,
        Md5 = leaf.TryGetProperty("md5", out var m) ? m.GetString() : null,
        Size = leaf.TryGetProperty("size", out var s) && s.TryGetInt64(out var v) ? v : 0,
    };

    // ---- classification & formatting --------------------------------------

    private static MapType ClassifyTextureKey(string key) => Import.MapClassifier.Classify(key);

    private static string Suffix(MapType map) => Import.MapClassifier.Suffix(map);

    private static string TypeParam(AssetKind k) => k switch
    {
        AssetKind.Texture => "textures",
        AssetKind.Model => "models",
        AssetKind.Hdri => "hdris",
        _ => "all",
    };

    private static IReadOnlyList<string> ReadStringArray(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList()
            : [];

    private static string ResLabel(int pixels) =>
        pixels % 1024 == 0 ? $"{pixels / 1024}k" : $"{(int)Math.Round(pixels / 1024.0)}k";

    /// <summary>Rank a resolution label like "4k" → 4 for ordering (16k &gt; 8k &gt; … &gt; 1k).</summary>
    internal static int ResRank(string label)
    {
        var s = label.TrimEnd('k', 'K');
        return int.TryParse(s, out var v) ? v : 0;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private sealed class ResComparer : IComparer<string>
    {
        public static readonly ResComparer Instance = new();
        public int Compare(string? x, string? y) => ResRank(x ?? "").CompareTo(ResRank(y ?? ""));
    }
}
