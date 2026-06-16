namespace SboxAssetLib.Core.Model;

/// <summary>The broad category of asset a provider can return.</summary>
public enum AssetKind
{
    Texture,
    Model,
    Hdri,
}

/// <summary>
/// The PBR channels an asset provides. Used to drive the UI badge row and to
/// wire the correct inputs/feature-flags when generating a Source 2 material.
/// </summary>
[Flags]
public enum MapType
{
    None = 0,
    Albedo = 1 << 0, // base colour / diffuse
    Normal = 1 << 1,
    Roughness = 1 << 2,
    Metalness = 1 << 3,
    AmbientOcclusion = 1 << 4,
    Height = 1 << 5, // displacement / height / parallax
    Opacity = 1 << 6,
    Emission = 1 << 7,
    Arm = 1 << 8, // packed AO + Roughness + Metalness (a.k.a. ORM)
    Specular = 1 << 9,
}

public enum NormalConvention
{
    OpenGl, // +Y / green-up
    DirectX, // -Y / green-down
}

public static class MapTypeExtensions
{
    /// <summary>An asset is considered PBR if it has a normal map plus a roughness source.</summary>
    public static bool IsPbr(this MapType maps) =>
        maps.HasFlag(MapType.Normal) && (maps.HasFlag(MapType.Roughness) || maps.HasFlag(MapType.Arm));

    public static IEnumerable<MapType> Channels(this MapType maps)
    {
        foreach (MapType v in Enum.GetValues<MapType>())
            if (v != MapType.None && maps.HasFlag(v))
                yield return v;
    }
}

/// <summary>Lightweight record used to render gallery cards quickly.</summary>
public sealed record AssetSummary
{
    public required string ProviderId { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required AssetKind Kind { get; init; }
    public string? ThumbnailUrl { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>Known map support. May be <see cref="MapType.None"/> until the detail is fetched.</summary>
    public MapType MapSupport { get; init; } = MapType.None;

    /// <summary>Highest resolution label the provider offers, e.g. "8k".</summary>
    public string? MaxResolution { get; init; }

    public bool IsPbr => MapSupport.IsPbr();
}

/// <summary>Full asset metadata, fetched on demand for the detail panel / import.</summary>
public sealed record AssetDetail
{
    public required AssetSummary Summary { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Authors { get; init; } = [];
    public IReadOnlyList<string> Resolutions { get; init; } = []; // e.g. ["1k","2k","4k","8k"]
    public IReadOnlyList<string> Formats { get; init; } = []; // e.g. ["png","jpg"] or ["fbx","gltf"]
    public string? License { get; init; } = "CC0";
    public string? SourceUrl { get; init; }
    public MapType MapSupport { get; init; }
}

/// <summary>The role a downloaded file plays once on disk.</summary>
public enum DownloadRole
{
    MapTexture, // a single PBR channel image
    Mesh, // a model mesh (fbx/gltf)
    Archive, // a zip that must be expanded then classified
    Dependency, // a file referenced by a mesh/archive (e.g. a texture in a gltf)
}

/// <summary>A concrete, resolvable file to download for a chosen asset+resolution.</summary>
public sealed record DownloadFile
{
    public required string Url { get; init; }
    public required string FileName { get; init; } // suggested local file name
    public required DownloadRole Role { get; init; }
    public MapType Map { get; init; } = MapType.None; // meaningful when Role == MapTexture
    public string? Md5 { get; init; }
    public long Size { get; init; }
}

public sealed record AssetQuery
{
    public string? Text { get; init; }
    public AssetKind? Kind { get; init; }
    public IReadOnlyList<string>? Categories { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public bool PbrOnly { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; } = 60;
}

public sealed record SearchPage
{
    public required IReadOnlyList<AssetSummary> Items { get; init; }
    public int Page { get; init; }
    public bool HasMore { get; init; }
    public int? Total { get; init; }
}

/// <summary>User preferences that influence which concrete files get resolved.</summary>
public sealed record FormatPrefs
{
    public const double DefaultModelImportScale = 0.3937;

    /// <summary>Preferred image formats in priority order (first available wins).</summary>
    public IReadOnlyList<string> ImageFormats { get; init; } = ["png", "jpg"];

    /// <summary>Preferred model formats in priority order.</summary>
    public IReadOnlyList<string> ModelFormats { get; init; } = ["fbx", "gltf", "glb"];

    public NormalConvention Normal { get; init; } = NormalConvention.OpenGl;

    /// <summary>Global ModelDoc scale for centimeter-authored sources in inch-based s&amp;box units.</summary>
    public double ModelImportScale { get; init; } = DefaultModelImportScale;

    public static FormatPrefs Default { get; } = new();
}
