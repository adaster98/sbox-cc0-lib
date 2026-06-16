using ImageMagick;
using SboxAssetLib.Core.Model;

namespace SboxAssetLib.Core.Import;

/// <summary>Prepares source texture files for material inputs that s&amp;box can compile directly.</summary>
public static class MaterialTextureNormalizer
{
    private static readonly HashSet<string> SafeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".tga",
        ".bmp",
    };

    public static bool IsEditorSafe(string path) => SafeExtensions.Contains(Path.GetExtension(path));

    public static string NormalizeMapFile(string sourcePath, string destinationWithoutExtension, MapType map)
    {
        if (IsEditorSafe(sourcePath))
        {
            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            var destination = destinationWithoutExtension + ext;
            if (SamePath(sourcePath, destination))
                return sourcePath;

            if (!Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(destination), StringComparison.Ordinal))
                File.Copy(sourcePath, destination, overwrite: true);
            return destination;
        }

        if (!Path.GetExtension(sourcePath).Equals(".exr", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unsupported {map} texture format: {Path.GetFileName(sourcePath)}");

        var pngPath = destinationWithoutExtension + ".png";
        using var image = new MagickImage(sourcePath);
        image.Format = MagickFormat.Png;
        image.Write(pngPath);
        return pngPath;
    }

    private static bool SamePath(string a, string b)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return Path.GetFullPath(a).Equals(Path.GetFullPath(b), comparison);
    }
}
