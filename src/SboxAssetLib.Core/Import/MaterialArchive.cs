using System.IO.Compression;
using SboxAssetLib.Core.Model;

namespace SboxAssetLib.Core.Import;

/// <summary>
/// Extracts a downloaded material archive (e.g. an ambientCG zip) and classifies the contained
/// images into a single best file per PBR channel, honouring the requested normal convention.
/// </summary>
public static class MaterialArchive
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".tga", ".exr", ".bmp"];

    public static IReadOnlyDictionary<MapType, string> ExtractMaps(
        string zipPath, string extractDir, NormalConvention normal)
    {
        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

        var images = Directory
            .EnumerateFiles(extractDir, "*", SearchOption.AllDirectories)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

        var result = new Dictionary<MapType, string>();

        // Non-normal channels: prefer the cleanest (shortest) file name; skip packed maps.
        foreach (var f in images)
        {
            var map = MapClassifier.Classify(Path.GetFileNameWithoutExtension(f));
            if (map is MapType.None or MapType.Arm or MapType.Normal)
                continue;
            if (!result.TryGetValue(map, out var existing)
                || Path.GetFileName(f).Length < Path.GetFileName(existing).Length)
                result[map] = f;
        }

        // Normal: choose the variant that matches the requested convention.
        var normals = images
            .Where(f => MapClassifier.Classify(Path.GetFileNameWithoutExtension(f)) == MapType.Normal)
            .ToList();
        if (normals.Count > 0)
        {
            var want = normal == NormalConvention.OpenGl ? "gl" : "dx";
            var avoid = normal == NormalConvention.OpenGl ? "dx" : "gl";
            var pick = normals.FirstOrDefault(f => Name(f).Contains(want))
                       ?? normals.FirstOrDefault(f => !Name(f).Contains(avoid))
                       ?? normals[0];
            result[MapType.Normal] = pick;
        }

        return result;

        static string Name(string f) => Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
    }
}
