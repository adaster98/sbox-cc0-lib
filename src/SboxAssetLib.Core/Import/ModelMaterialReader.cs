using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SboxAssetLib.Core.Model;

namespace SboxAssetLib.Core.Import;

/// <summary>A texture reference attached to a source model material.</summary>
public sealed record ModelMaterialTexture(string Path, MapType Map);

/// <summary>A named source material slot and the textures assigned to it by the mesh.</summary>
public sealed record ModelMaterialSlot(string Name, IReadOnlyList<ModelMaterialTexture> Textures);

/// <summary>Reads the material metadata needed to preserve source-model material slots.</summary>
public static partial class ModelMaterialReader
{
    private static readonly byte[] FbxMagic = "Kaydara FBX Binary  \0\x1a\0"u8.ToArray();

    public static IReadOnlyList<ModelMaterialSlot> Read(string meshPath)
    {
        try
        {
            return Path.GetExtension(meshPath).ToLowerInvariant() switch
            {
                ".fbx" => ReadFbx(meshPath),
                ".gltf" => ReadGltf(File.ReadAllBytes(meshPath)),
                ".glb" => ReadGlb(meshPath),
                _ => [],
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException
                                       or DecoderFallbackException or InvalidOperationException
                                       or ArgumentException or FormatException or OverflowException)
        {
            // Material metadata is optional; the importer can still use its legacy global material.
            return [];
        }
    }

    private static IReadOnlyList<ModelMaterialSlot> ReadGltf(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var images = ReadGltfImages(root);
        var textureSources = ReadGltfTextureSources(root);
        if (!root.TryGetProperty("materials", out var materials) || materials.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<ModelMaterialSlot>();
        var materialIndex = 0;
        foreach (var material in materials.EnumerateArray())
        {
            var name = material.TryGetProperty("name", out var nameNode) && !string.IsNullOrWhiteSpace(nameNode.GetString())
                ? nameNode.GetString()!
                : $"material_{materialIndex + 1}";
            var bindings = new List<ModelMaterialTexture>();

            if (material.TryGetProperty("pbrMetallicRoughness", out var pbr))
            {
                AddGltfTexture(pbr, "baseColorTexture", MapType.Albedo, textureSources, images, bindings);
                AddGltfTexture(pbr, "metallicRoughnessTexture", MapType.Arm, textureSources, images, bindings);
            }
            AddGltfTexture(material, "normalTexture", MapType.Normal, textureSources, images, bindings);
            AddGltfTexture(material, "occlusionTexture", MapType.AmbientOcclusion, textureSources, images, bindings);
            AddGltfTexture(material, "emissiveTexture", MapType.Emission, textureSources, images, bindings);

            result.Add(new ModelMaterialSlot(name, bindings));
            materialIndex++;
        }
        return result;
    }

    private static IReadOnlyList<string?> ReadGltfImages(JsonElement root)
    {
        if (!root.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
            return [];
        return images.EnumerateArray()
            .Select(image => image.TryGetProperty("uri", out var uri) ? DecodeUri(uri.GetString()) : null)
            .ToList();
    }

    private static IReadOnlyList<int> ReadGltfTextureSources(JsonElement root)
    {
        if (!root.TryGetProperty("textures", out var textures) || textures.ValueKind != JsonValueKind.Array)
            return [];
        return textures.EnumerateArray()
            .Select(texture => texture.TryGetProperty("source", out var source) ? source.GetInt32() : -1)
            .ToList();
    }

    private static void AddGltfTexture(
        JsonElement owner, string property, MapType map,
        IReadOnlyList<int> textureSources, IReadOnlyList<string?> images,
        List<ModelMaterialTexture> bindings)
    {
        if (!owner.TryGetProperty(property, out var textureInfo)
            || !textureInfo.TryGetProperty("index", out var indexNode))
            return;
        var textureIndex = indexNode.GetInt32();
        if (textureIndex < 0 || textureIndex >= textureSources.Count)
            return;
        var imageIndex = textureSources[textureIndex];
        if (imageIndex < 0 || imageIndex >= images.Count || string.IsNullOrWhiteSpace(images[imageIndex]))
            return;
        bindings.Add(new ModelMaterialTexture(images[imageIndex]!, map));
    }

    private static string? DecodeUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return null;
        return Uri.UnescapeDataString(uri).Replace('\\', '/');
    }

    private static IReadOnlyList<ModelMaterialSlot> ReadGlb(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        if (reader.ReadUInt32() != 0x46546C67 || reader.ReadUInt32() != 2)
            throw new InvalidDataException("Unsupported GLB header.");
        _ = reader.ReadUInt32();
        while (stream.Position + 8 <= stream.Length)
        {
            var length = reader.ReadUInt32();
            var type = reader.ReadUInt32();
            if (length > int.MaxValue || stream.Position + length > stream.Length)
                throw new InvalidDataException("Invalid GLB chunk length.");
            var content = reader.ReadBytes((int)length);
            if (type == 0x4E4F534A)
                return ReadGltf(content);
        }
        return [];
    }

    private static IReadOnlyList<ModelMaterialSlot> ReadFbx(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[FbxMagic.Length];
        if (stream.Read(magic) != magic.Length)
            return [];
        stream.Position = 0;
        return magic.SequenceEqual(FbxMagic) ? ReadBinaryFbx(stream) : ReadAsciiFbx(path);
    }

    private static IReadOnlyList<ModelMaterialSlot> ReadBinaryFbx(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        stream.Position = FbxMagic.Length;
        var version = reader.ReadUInt32();
        var roots = new List<FbxNode>();
        while (stream.Position < stream.Length)
        {
            var node = ReadFbxNode(reader, version);
            if (node is null)
                break;
            roots.Add(node);
        }
        return BuildFbxMaterials(roots);
    }

    private static FbxNode? ReadFbxNode(BinaryReader reader, uint version)
    {
        var wide = version >= 7500;
        var endOffset = wide ? reader.ReadUInt64() : reader.ReadUInt32();
        var propertyCount = wide ? reader.ReadUInt64() : reader.ReadUInt32();
        _ = wide ? reader.ReadUInt64() : reader.ReadUInt32();
        var nameLength = reader.ReadByte();
        if (endOffset == 0)
            return null;
        if (endOffset > (ulong)reader.BaseStream.Length || propertyCount > int.MaxValue)
            throw new InvalidDataException("Invalid FBX node header.");

        var name = Encoding.UTF8.GetString(reader.ReadBytes(nameLength));
        var properties = new List<object?>((int)propertyCount);
        for (ulong i = 0; i < propertyCount; i++)
            properties.Add(ReadFbxProperty(reader));

        var children = new List<FbxNode>();
        var nullRecordSize = wide ? 25 : 13;
        while ((ulong)reader.BaseStream.Position + (ulong)nullRecordSize < endOffset)
        {
            var child = ReadFbxNode(reader, version);
            if (child is null)
                break;
            children.Add(child);
        }
        reader.BaseStream.Position = (long)endOffset;
        return new FbxNode(name, properties, children);
    }

    private static object? ReadFbxProperty(BinaryReader reader)
    {
        return (char)reader.ReadByte() switch
        {
            'Y' => reader.ReadInt16(),
            'C' => reader.ReadByte() != 0,
            'I' => reader.ReadInt32(),
            'F' => reader.ReadSingle(),
            'D' => reader.ReadDouble(),
            'L' => reader.ReadInt64(),
            'S' => Encoding.UTF8.GetString(reader.ReadBytes(CheckedLength(reader.ReadUInt32()))),
            'R' => reader.ReadBytes(CheckedLength(reader.ReadUInt32())),
            'f' or 'd' or 'l' or 'i' or 'b' or 'c' => SkipFbxArray(reader),
            var type => throw new InvalidDataException($"Unsupported FBX property type '{type}'."),
        };
    }

    private static object? SkipFbxArray(BinaryReader reader)
    {
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        var compressedLength = reader.ReadUInt32();
        reader.BaseStream.Seek(compressedLength, SeekOrigin.Current);
        return null;
    }

    private static int CheckedLength(uint length) => length <= int.MaxValue
        ? (int)length
        : throw new InvalidDataException("FBX property is too large.");

    private static IReadOnlyList<ModelMaterialSlot> ReadAsciiFbx(string path)
    {
        var text = File.ReadAllText(path);
        var materials = AsciiMaterialRegex().Matches(text)
            .Select(match => (Id: long.Parse(match.Groups["id"].Value), Name: match.Groups["name"].Value))
            .ToDictionary(x => x.Id, x => CleanFbxName(x.Name));
        var textures = new Dictionary<long, string>();
        foreach (Match match in AsciiTextureRegex().Matches(text))
            textures[long.Parse(match.Groups["id"].Value)] = match.Groups["path"].Value.Replace("\\\\", "/");
        var connections = AsciiConnectionRegex().Matches(text).Select(match => new FbxConnection(
            match.Groups["kind"].Value,
            long.Parse(match.Groups["source"].Value),
            long.Parse(match.Groups["target"].Value),
            match.Groups["property"].Value)).ToList();
        return BuildFbxMaterials(materials, textures, connections);
    }

    private static IReadOnlyList<ModelMaterialSlot> BuildFbxMaterials(IReadOnlyList<FbxNode> roots)
    {
        var objects = roots.FirstOrDefault(node => node.Name == "Objects")?.Children ?? [];
        var materials = objects.Where(node => node.Name == "Material" && node.Properties.Count >= 2)
            .ToDictionary(node => Convert.ToInt64(node.Properties[0]), node => CleanFbxName((string)node.Properties[1]!));
        var textures = objects.Where(node => node.Name == "Texture" && node.Properties.Count >= 1)
            .Select(node => (Id: Convert.ToInt64(node.Properties[0]), Path: FindFbxPath(node)))
            .Where(texture => !string.IsNullOrWhiteSpace(texture.Path))
            .ToDictionary(texture => texture.Id, texture => texture.Path!);
        var connectionNodes = roots.FirstOrDefault(node => node.Name == "Connections")?.Children ?? [];
        var connections = connectionNodes.Where(node => node.Name == "C" && node.Properties.Count >= 3)
            .Select(node => new FbxConnection(
                node.Properties[0] as string ?? "",
                Convert.ToInt64(node.Properties[1]),
                Convert.ToInt64(node.Properties[2]),
                node.Properties.Count >= 4 ? node.Properties[3] as string ?? "" : ""))
            .ToList();
        return BuildFbxMaterials(materials, textures, connections);
    }

    private static string? FindFbxPath(FbxNode texture)
    {
        return texture.Children.FirstOrDefault(node => node.Name == "RelativeFilename")?.Properties.FirstOrDefault() as string
               ?? texture.Children.FirstOrDefault(node => node.Name == "FileName")?.Properties.FirstOrDefault() as string;
    }

    private static IReadOnlyList<ModelMaterialSlot> BuildFbxMaterials(
        IReadOnlyDictionary<long, string> materials,
        IReadOnlyDictionary<long, string> textures,
        IReadOnlyList<FbxConnection> connections)
    {
        var bindings = materials.Keys.ToDictionary(id => id, _ => new List<ModelMaterialTexture>());
        foreach (var connection in connections)
        {
            if (!bindings.TryGetValue(connection.Target, out var materialBindings)
                || !textures.TryGetValue(connection.Source, out var texturePath))
                continue;
            var filenameMap = MapClassifier.Classify(Path.GetFileNameWithoutExtension(texturePath));
            var map = filenameMap != MapType.None ? filenameMap : ClassifyFbxProperty(connection.Property);
            materialBindings.Add(new ModelMaterialTexture(texturePath.Replace('\\', '/'), map));
        }
        return materials.Select(material => new ModelMaterialSlot(material.Value, bindings[material.Key])).ToList();
    }

    private static string CleanFbxName(string name)
    {
        var terminator = name.IndexOf('\0');
        if (terminator >= 0)
            name = name[..terminator];
        var separator = name.IndexOf("::", StringComparison.Ordinal);
        return separator >= 0 ? name[(separator + 2)..] : name;
    }

    private static MapType ClassifyFbxProperty(string property)
    {
        var value = property.ToLowerInvariant();
        if (value.Contains("normal") || value.Contains("bump")) return MapType.Normal;
        if (value.Contains("rough") || value.Contains("shininess")) return MapType.Roughness;
        if (value.Contains("metal") || value.Contains("reflection")) return MapType.Metalness;
        if (value.Contains("transparent") || value.Contains("opacity") || value.Contains("alpha")) return MapType.Opacity;
        if (value.Contains("ambient") || value.Contains("occlusion")) return MapType.AmbientOcclusion;
        if (value.Contains("emiss")) return MapType.Emission;
        if (value.Contains("specular")) return MapType.Specular;
        if (value.Contains("diffuse") || value.Contains("color")) return MapType.Albedo;
        return MapType.None;
    }

    private sealed record FbxNode(string Name, IReadOnlyList<object?> Properties, IReadOnlyList<FbxNode> Children);
    private sealed record FbxConnection(string Kind, long Source, long Target, string Property);

    [GeneratedRegex("Material:\\s*(?<id>-?\\d+)\\s*,\\s*\"Material::(?<name>[^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex AsciiMaterialRegex();

    [GeneratedRegex("Texture:\\s*(?<id>-?\\d+)[\\s\\S]*?RelativeFilename:\\s*\"(?<path>[^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex AsciiTextureRegex();

    [GeneratedRegex("C:\\s*\"(?<kind>O[OP])\"\\s*,\\s*(?<source>-?\\d+)\\s*,\\s*(?<target>-?\\d+)(?:\\s*,\\s*\"(?<property>[^\"]*)\")?", RegexOptions.CultureInvariant)]
    private static partial Regex AsciiConnectionRegex();
}
