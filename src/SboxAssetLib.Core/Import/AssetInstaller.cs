using System.Text.Json;
using System.Text.Json.Serialization;
using SboxAssetLib.Core.Download;
using SboxAssetLib.Core.Model;
using SboxAssetLib.Core.Providers;

namespace SboxAssetLib.Core.Import;

public sealed record InstallOptions
{
    /// <summary>Addon root to install into — a mountable library or an open project's addon.</summary>
    public required string AddonRoot { get; init; }

    /// <summary>Override the auto-derived category folder.</summary>
    public string? CategoryOverride { get; init; }

    public FormatPrefs Prefs { get; init; } = FormatPrefs.Default;

    /// <summary>Maintain a library.json manifest at the addon root (used by library mode).</summary>
    public bool WriteManifest { get; init; } = true;
}

public sealed record InstalledAsset
{
    public required string ProviderId { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required AssetKind Kind { get; init; }
    public required string Category { get; init; }
    public required string Resolution { get; init; }

    /// <summary>Addon-relative directory the asset was written to.</summary>
    public required string RelativeDir { get; init; }

    /// <summary>Addon-relative path to the compilable asset (.vmat or .vmdl).</summary>
    public required string PrimaryAsset { get; init; }

    public required IReadOnlyList<string> Files { get; init; }
    public string License { get; init; } = "CC0";
    public string? SourceUrl { get; init; }
}

/// <summary>
/// Downloads an asset's files and writes the Source 2 wrapper assets into an addon, laid out
/// by type (materials/ vs models/) and category. Shared by the standalone app's library mode
/// and by the s&amp;box plugin's "import into project" path.
/// </summary>
public sealed class AssetInstaller
{
    private readonly DownloadManager _downloader;

    public AssetInstaller(DownloadManager downloader) => _downloader = downloader;

    public async Task<InstalledAsset> InstallAsync(
        IAssetProvider provider,
        AssetDetail detail,
        string resolution,
        InstallOptions opts,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var s = detail.Summary;
        var category = opts.CategoryOverride ?? CategoryMapper.Map(s.Categories.Concat(s.Tags));
        var files = await provider.ResolveFilesAsync(s.Id, s.Kind, resolution, opts.Prefs, ct).ConfigureAwait(false);

        var typeRoot = s.Kind == AssetKind.Model ? "models" : "materials";
        var relDir = $"{typeRoot}/{category}/{s.Id}";
        var destDir = Path.Combine(opts.AddonRoot, relDir);

        var fetched = await _downloader.DownloadAllAsync(files, destDir, progress, ct).ConfigureAwait(false);
        var written = fetched.Select(f => Rel(relDir, f.File.FileName)).ToList();

        string primary = s.Kind == AssetKind.Model
            ? await WriteModelAsync(s.Id, relDir, destDir, fetched, opts.Prefs, written, ct).ConfigureAwait(false)
            : await WriteTextureAsync(s.Id, relDir, destDir, fetched, opts.Prefs, written, ct).ConfigureAwait(false);

        var installed = new InstalledAsset
        {
            ProviderId = s.ProviderId,
            Id = s.Id,
            Name = s.Name,
            Kind = s.Kind,
            Category = category,
            Resolution = resolution,
            RelativeDir = relDir,
            PrimaryAsset = primary,
            Files = written,
            License = detail.License ?? "CC0",
            SourceUrl = detail.SourceUrl,
        };

        if (opts.WriteManifest)
        {
            var manifest = await LibraryManifest.LoadAsync(opts.AddonRoot, ct).ConfigureAwait(false);
            manifest.Upsert(ManifestEntry.From(installed));
            await manifest.SaveAsync(opts.AddonRoot, ct).ConfigureAwait(false);
        }

        return installed;
    }

    private static async Task<string> WriteTextureAsync(
        string id, string relDir, string destDir, IReadOnlyList<FetchedFile> fetched,
        FormatPrefs prefs, List<string> written, CancellationToken ct)
    {
        // Direct per-channel files (e.g. Poly Haven).
        var maps = fetched
            .Where(x => x.File.Role == DownloadRole.MapTexture)
            .ToDictionary(x => x.File.Map, x => Rel(relDir, x.File.FileName));

        // Archives (e.g. ambientCG zips): extract, classify, and rename the chosen map per channel.
        foreach (var archive in fetched.Where(x => x.File.Role == DownloadRole.Archive))
        {
            var extractDir = Path.Combine(destDir, "_extract");
            foreach (var (map, src) in MaterialArchive.ExtractMaps(archive.LocalPath, extractDir, prefs.Normal))
            {
                var destName = $"{id}_{MapClassifier.Suffix(map)}{Path.GetExtension(src).ToLowerInvariant()}";
                File.Copy(src, Path.Combine(destDir, destName), overwrite: true);
                maps[map] = Rel(relDir, destName);
                written.Add(Rel(relDir, destName));
            }
            Directory.Delete(extractDir, recursive: true);
            File.Delete(archive.LocalPath);
            written.Remove(Rel(relDir, archive.File.FileName));
        }

        var vmat = VmatWriter.Write(maps, prefs.Normal);
        await File.WriteAllTextAsync(Path.Combine(destDir, $"{id}.vmat"), vmat, ct).ConfigureAwait(false);
        var primary = $"{relDir}/{id}.vmat";
        written.Add(primary);
        return primary;
    }

    private static async Task<string> WriteModelAsync(
        string id, string relDir, string destDir, IReadOnlyList<FetchedFile> fetched,
        FormatPrefs prefs, List<string> written, CancellationToken ct)
    {
        var mesh = fetched.First(x => x.File.Role == DownloadRole.Mesh);
        var meshRel = Rel(relDir, mesh.File.FileName);

        // Build a material from the mesh's dependency textures so it isn't grey on import.
        var depMaps = new Dictionary<MapType, string>();
        foreach (var dep in fetched.Where(x => x.File.Role == DownloadRole.Dependency))
        {
            var map = MapClassifier.Classify(Path.GetFileNameWithoutExtension(dep.File.FileName));
            if (map is MapType.None or MapType.Arm || depMaps.ContainsKey(map))
                continue;
            depMaps[map] = Rel(relDir, dep.File.FileName);
        }

        if (depMaps.Count > 0)
        {
            var vmat = VmatWriter.Write(depMaps, prefs.Normal);
            await File.WriteAllTextAsync(Path.Combine(destDir, $"{id}.vmat"), vmat, ct).ConfigureAwait(false);
            written.Add($"{relDir}/{id}.vmat");
        }

        var vmdl = VmdlWriter.Write(id, meshRel);
        await File.WriteAllTextAsync(Path.Combine(destDir, $"{id}.vmdl"), vmdl, ct).ConfigureAwait(false);
        var primary = $"{relDir}/{id}.vmdl";
        written.Add(primary);
        return primary;
    }

    private static string Rel(string relDir, string fileName) => $"{relDir}/{fileName.Replace('\\', '/')}";
}

// ---- Library manifest -----------------------------------------------------

public sealed record ManifestEntry(
    string Provider, string Id, string Name, string Kind, string Category,
    string Resolution, string License, string? Source, string PrimaryAsset,
    IReadOnlyList<string> Files, DateTimeOffset InstalledAt)
{
    public static ManifestEntry From(InstalledAsset a) => new(
        a.ProviderId, a.Id, a.Name, a.Kind.ToString(), a.Category, a.Resolution,
        a.License, a.SourceUrl, a.PrimaryAsset, a.Files, DateTimeOffset.UtcNow);
}

/// <summary>The <c>library.json</c> manifest tracking installed assets (for dedupe + attribution).</summary>
public sealed class LibraryManifest
{
    public const string FileName = "library.json";

    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("assets")] public List<ManifestEntry> Assets { get; set; } = [];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public bool Contains(string provider, string id) =>
        Assets.Any(a => a.Provider == provider && a.Id == id);

    public void Upsert(ManifestEntry entry)
    {
        Assets.RemoveAll(a => a.Provider == entry.Provider && a.Id == entry.Id);
        Assets.Add(entry);
    }

    public static async Task<LibraryManifest> LoadAsync(string addonRoot, CancellationToken ct = default)
    {
        var path = Path.Combine(addonRoot, FileName);
        if (!File.Exists(path))
            return new LibraryManifest();
        await using var s = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<LibraryManifest>(s, JsonOpts, ct).ConfigureAwait(false)
               ?? new LibraryManifest();
    }

    public async Task SaveAsync(string addonRoot, CancellationToken ct = default)
    {
        Directory.CreateDirectory(addonRoot);
        var path = Path.Combine(addonRoot, FileName);
        await using var s = File.Create(path);
        await JsonSerializer.SerializeAsync(s, this, JsonOpts, ct).ConfigureAwait(false);
    }
}
