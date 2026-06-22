using System.Text;
using SboxAssetLib.Core.Download;
using SboxAssetLib.Core.Import;
using SboxAssetLib.Core.Model;

internal static class MaterialImportSmoke
{
    public static async Task RunAsync(string? fenceSource)
    {
        var root = Path.Combine(Path.GetTempPath(), "sbox-asset-lib-material-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await VerifySyntheticFormatsAsync(root);
            VerifyVmdlCompatibility();
            if (!string.IsNullOrWhiteSpace(fenceSource))
                await VerifyFenceImportAsync(root, fenceSource);
            Console.WriteLine("Material import smoke checks passed.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task VerifySyntheticFormatsAsync(string root)
    {
        const string json = """
            {
              "asset": { "version": "2.0" },
              "images": [
                { "uri": "textures/Mat_A_color.png" },
                { "uri": "textures/Mat_A_rough.png" }
              ],
              "textures": [ { "source": 0 }, { "source": 1 } ],
              "materials": [
                { "name": "Mat/A", "pbrMetallicRoughness": { "baseColorTexture": { "index": 0 } } },
                { "name": "Mat:A", "pbrMetallicRoughness": { "baseColorTexture": { "index": 1 } } },
                { "name": "Empty" }
              ]
            }
            """;

        var gltf = Path.Combine(root, "synthetic.gltf");
        await File.WriteAllTextAsync(gltf, json);
        AssertSlots(ModelMaterialReader.Read(gltf), "glTF");

        var glb = Path.Combine(root, "synthetic.glb");
        await File.WriteAllBytesAsync(glb, MakeGlb(json));
        AssertSlots(ModelMaterialReader.Read(glb), "GLB");

        var modelDir = Path.Combine(root, "synthetic-model");
        Directory.CreateDirectory(Path.Combine(modelDir, "textures"));
        File.Copy(gltf, Path.Combine(modelDir, "synthetic.gltf"));
        await File.WriteAllBytesAsync(Path.Combine(modelDir, "textures", "Mat_A_color.png"), TinyPng);
        await File.WriteAllBytesAsync(Path.Combine(modelDir, "textures", "Mat_A_rough.png"), TinyPng);

        var fetched = new List<FetchedFile>
        {
            Fetched("synthetic.gltf", DownloadRole.Mesh, Path.Combine(modelDir, "synthetic.gltf")),
            Fetched("textures/Mat_A_color.png", DownloadRole.Dependency, Path.Combine(modelDir, "textures", "Mat_A_color.png")),
            Fetched("textures/Mat_A_rough.png", DownloadRole.Dependency, Path.Combine(modelDir, "textures", "Mat_A_rough.png")),
        };
        var written = fetched.Select(file => $"models/test/synthetic/{file.File.FileName}").ToList();
        await AssetInstaller.WriteModelAsync(
            "synthetic", "models/test/synthetic", modelDir, fetched, FormatPrefs.Default, written, default);

        Require(File.Exists(Path.Combine(modelDir, "Mat_A.vmat")), "first sanitized VMAT was not generated");
        Require(File.Exists(Path.Combine(modelDir, "Mat_A_2.vmat")), "colliding VMAT name was not made unique");
        var fallback = await File.ReadAllTextAsync(Path.Combine(modelDir, "Empty.vmat"));
        Require(!fallback.Contains("TextureColor", StringComparison.Ordinal), "empty slot VMAT should be a plain fallback");
        var vmdl = await File.ReadAllTextAsync(Path.Combine(modelDir, "synthetic.vmdl"));
        Require(vmdl.Contains("use_global_default = false", StringComparison.Ordinal), "multi-material VMDL still uses a global default");
        Require(vmdl.Contains("from = \"Mat/A\"", StringComparison.Ordinal), "first exact material name is missing");
        Require(vmdl.Contains("to = \"models/test/synthetic/Mat_A_2.vmat\"", StringComparison.Ordinal), "collision-safe remap is missing");
        Require(written.Count(path => path.EndsWith(".vmat", StringComparison.OrdinalIgnoreCase)) == 3,
            "generated VMATs were not all added to the written-file list");

        await VerifySingleMaterialImportAsync(root);
    }

    private static async Task VerifySingleMaterialImportAsync(string root)
    {
        const string json = """
            {
              "asset": { "version": "2.0" },
              "images": [ { "uri": "single_color.png" } ],
              "textures": [ { "source": 0 } ],
              "materials": [
                { "name": "Single", "pbrMetallicRoughness": { "baseColorTexture": { "index": 0 } } }
              ]
            }
            """;
        var modelDir = Path.Combine(root, "single-model");
        Directory.CreateDirectory(modelDir);
        var mesh = Path.Combine(modelDir, "single.gltf");
        var color = Path.Combine(modelDir, "single_color.png");
        await File.WriteAllTextAsync(mesh, json);
        await File.WriteAllBytesAsync(color, TinyPng);
        var fetched = new List<FetchedFile>
        {
            Fetched("single.gltf", DownloadRole.Mesh, mesh),
            Fetched("single_color.png", DownloadRole.Dependency, color),
        };
        var written = fetched.Select(file => $"models/test/single/{file.File.FileName}").ToList();
        await AssetInstaller.WriteModelAsync(
            "single", "models/test/single", modelDir, fetched, FormatPrefs.Default, written, default);

        Require(File.Exists(Path.Combine(modelDir, "single.vmat")), "single-material VMAT naming changed");
        var vmdl = await File.ReadAllTextAsync(Path.Combine(modelDir, "single.vmdl"));
        Require(vmdl.Contains("use_global_default = true", StringComparison.Ordinal),
            "single-material import no longer uses the global default");
        Require(vmdl.Contains("global_default_material = \"models/test/single/single.vmat\"", StringComparison.Ordinal),
            "single-material global path changed");
    }

    private static async Task VerifyFenceImportAsync(string root, string source)
    {
        var sourceDir = Path.GetFullPath(source);
        var modelDir = Path.Combine(root, "fence");
        CopySourceFiles(sourceDir, modelDir);

        var fbx = Directory.EnumerateFiles(modelDir, "*.fbx").Single();
        var slots = ModelMaterialReader.Read(fbx);
        Require(slots.Select(slot => slot.Name).SequenceEqual([
            "modular_chainlink_fence_posts", "modular_chainlink_fence_wire"]),
            $"binary FBX material names were not read in source order: [{string.Join(", ", slots.Select(slot => slot.Name))}]");

        var fetched = new List<FetchedFile>
        {
            Fetched(Path.GetFileName(fbx), DownloadRole.Mesh, fbx),
        };
        fetched.AddRange(Directory.EnumerateFiles(Path.Combine(modelDir, "textures"))
            .Where(path => !path.EndsWith("_c", StringComparison.OrdinalIgnoreCase))
            .Select(path => Fetched(
                Path.GetRelativePath(modelDir, path).Replace('\\', '/'), DownloadRole.Dependency, path)));
        var relDir = "models/misc/modular_chainlink_fence";
        var written = fetched.Select(file => $"{relDir}/{file.File.FileName}").ToList();
        await AssetInstaller.WriteModelAsync(
            "modular_chainlink_fence", relDir, modelDir, fetched, FormatPrefs.Default, written, default);

        var posts = await File.ReadAllTextAsync(Path.Combine(modelDir, "modular_chainlink_fence_posts.vmat"));
        var wire = await File.ReadAllTextAsync(Path.Combine(modelDir, "modular_chainlink_fence_wire.vmat"));
        var vmdl = await File.ReadAllTextAsync(Path.Combine(modelDir, "modular_chainlink_fence.vmdl"));
        Require(posts.Contains("posts_diff", StringComparison.Ordinal) && !posts.Contains("wire_diff", StringComparison.Ordinal),
            "posts material did not retain its own texture set");
        Require(wire.Contains("wire_diff", StringComparison.Ordinal) && !wire.Contains("posts_diff", StringComparison.Ordinal),
            "wire material did not retain its own texture set");
        Require(!posts.Contains("F_ALPHA_TEST", StringComparison.Ordinal) && wire.Contains("F_ALPHA_TEST", StringComparison.Ordinal),
            "alpha testing was not isolated to the wire material");
        Require(vmdl.Contains("from = \"modular_chainlink_fence_posts\"", StringComparison.Ordinal)
                && vmdl.Contains("from = \"modular_chainlink_fence_wire\"", StringComparison.Ordinal),
            "fence material remaps are incomplete");
        Require(vmdl.Contains("use_global_default = false", StringComparison.Ordinal),
            "fence VMDL still globally replaces materials");
    }

    private static void VerifyVmdlCompatibility()
    {
        var legacy = VmdlWriter.Write("single", "models/single.fbx", "models/single.vmat");
        Require(legacy.Contains("remaps = [  ]", StringComparison.Ordinal), "legacy VMDL remaps changed");
        Require(legacy.Contains("use_global_default = true", StringComparison.Ordinal), "legacy global material was disabled");
        Require(legacy.Contains("global_default_material = \"models/single.vmat\"", StringComparison.Ordinal),
            "legacy global material path changed");
    }

    private static void AssertSlots(IReadOnlyList<ModelMaterialSlot> slots, string format)
    {
        Require(slots.Count == 3, $"{format} material count was not preserved");
        Require(slots[0].Name == "Mat/A" && slots[0].Textures.Single().Map == MapType.Albedo,
            $"{format} base-color binding was not read");
        Require(slots[2].Name == "Empty" && slots[2].Textures.Count == 0,
            $"{format} empty material slot was not preserved");
    }

    private static FetchedFile Fetched(string fileName, DownloadRole role, string localPath) =>
        new(new DownloadFile { Url = "https://example.invalid/fixture", FileName = fileName, Role = role }, localPath);

    private static byte[] MakeGlb(string json)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var paddedLength = (jsonBytes.Length + 3) & ~3;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x46546C67u);
        writer.Write(2u);
        writer.Write((uint)(12 + 8 + paddedLength));
        writer.Write((uint)paddedLength);
        writer.Write(0x4E4F534Au);
        writer.Write(jsonBytes);
        for (var i = jsonBytes.Length; i < paddedLength; i++)
            writer.Write((byte)' ');
        return stream.ToArray();
    }

    private static void CopySourceFiles(string sourceDir, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var source in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
                     .Where(path => path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)
                                    || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(sourceDir, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    // One opaque 1x1 PNG; texture contents do not matter to the writer.
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M/wHwAF/gL+XwVqWQAAAABJRU5ErkJggg==");
}
