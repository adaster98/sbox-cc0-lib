using SboxAssetLib.Core.Model;

namespace SboxAssetLib.Core.Import;

/// <summary>
/// Classifies a provider map key or texture file name onto a <see cref="MapType"/>, and
/// supplies the canonical file-name suffix used when writing assets. Shared by provider
/// adapters and the installer so naming stays consistent everywhere.
/// </summary>
public static class MapClassifier
{
    /// <summary>Classify a key/filename like "nor_gl", "Rough", or "CheeseBox_metallic_1k.jpg".</summary>
    public static MapType Classify(string key)
    {
        var k = key.ToLowerInvariant();
        if (k.Contains("nor")) return MapType.Normal;
        if (k == "arm") return MapType.Arm;
        if (k is "diffuse" or "albedo" or "col"
            || k.Contains("diff") || k.Contains("albedo") || k.Contains("color") || k.Contains("colour"))
            return MapType.Albedo;
        if (k.Contains("rough")) return MapType.Roughness;
        if (k.Contains("metal")) return MapType.Metalness; // matches "metal" and "metallic"
        if (k == "ao" || k.Contains("ambient") || k.Contains("occlusion")) return MapType.AmbientOcclusion;
        if (k.Contains("disp") || k.Contains("height") || k.Contains("bump")) return MapType.Height;
        if (k.Contains("opacity") || k.Contains("alpha")) return MapType.Opacity;
        if (k.Contains("emiss") || k.Contains("selfillum")) return MapType.Emission;
        if (k.Contains("spec")) return MapType.Specular;
        return MapType.None;
    }

    public static string Suffix(MapType map) => map switch
    {
        MapType.Albedo => "color",
        MapType.Normal => "normal",
        MapType.Roughness => "rough",
        MapType.Metalness => "metal",
        MapType.AmbientOcclusion => "ao",
        MapType.Height => "height",
        MapType.Arm => "arm",
        MapType.Opacity => "opacity",
        MapType.Emission => "selfillum",
        MapType.Specular => "spec",
        _ => "tex",
    };
}
