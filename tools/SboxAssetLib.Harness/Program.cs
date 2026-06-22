using SboxAssetLib.Core.Download;
using SboxAssetLib.Core.Import;
using SboxAssetLib.Core.Model;
using SboxAssetLib.Core.Providers;
using SboxAssetLib.Core.Providers.AmbientCg;
using SboxAssetLib.Core.Providers.Cc0Textures;
using SboxAssetLib.Core.Providers.PolyHaven;
using SboxAssetLib.Core.Providers.TextureCan;

// End-to-end smoke test against the live Poly Haven API:
//   search -> detail (PBR maps) -> install (resolve + download + write .vmat/.vmdl + manifest)
// into a mountable library, then dump the resulting folder tree + manifest.

if (args.FirstOrDefault() == "--material-smoke")
{
    await MaterialImportSmoke.RunAsync(args.Skip(1).FirstOrDefault());
    return;
}

var query = args.Length > 0 ? args[0] : "rock";
var libDir = Path.Combine(Path.GetTempPath(), "sbox-asset-lib-test", "MyLibrary");

using var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("sbox-asset-lib-harness/0.1 (+https://github.com/)");
var polyHaven = new PolyHavenProvider(http);
var ambientCg = new AmbientCgProvider(http);
var cc0Textures = new Cc0TexturesProvider(http);
var textureCan = new TextureCanProvider(http);
var installer = new AssetInstaller(new DownloadManager(http));
var opts = new InstallOptions { AddonRoot = libDir };

async Task Install(IAssetProvider provider, string text, AssetKind kind, string res)
{
    Console.WriteLine($"== [{provider.DisplayName}] {kind}: searching \"{text}\" ==");
    var page = await provider.SearchAsync(new AssetQuery { Text = text, Kind = kind, PageSize = 1 });
    if (page.Items.Count == 0) { Console.WriteLine("  no results"); return; }
    var pick = page.Items[0];
    Console.WriteLine($"  summary maps (from search): {pick.MapSupport}");
    var detail = await provider.GetDetailAsync(pick.Id, kind);
    Console.WriteLine($"  {pick.Id}: maps={detail.MapSupport} pbr={detail.MapSupport.IsPbr()} res=[{string.Join(",", detail.Resolutions)}] fmt=[{string.Join(",", detail.Formats)}]");
    var chosen = detail.Resolutions.Contains(res) ? res : detail.Resolutions.FirstOrDefault() ?? res;
    var installed = await installer.InstallAsync(provider, detail, chosen, opts);
    Console.WriteLine($"  -> {installed.Category}/{installed.Id} @ {installed.Resolution}, primary: {installed.PrimaryAsset} ({installed.Files.Count} files)");
}

await Install(polyHaven, query, AssetKind.Texture, "1k");
await Install(polyHaven, "wooden", AssetKind.Model, "1k");
await Install(ambientCg, query, AssetKind.Texture, "1k");
await Install(cc0Textures, query, AssetKind.Texture, "4k");
await Install(textureCan, query, AssetKind.Texture, "1k");
await Install(textureCan, "coin", AssetKind.Model, "4k");

Console.WriteLine($"\n== Library tree: {libDir} ==");
foreach (var f in Directory.EnumerateFiles(libDir, "*", SearchOption.AllDirectories).OrderBy(x => x))
    Console.WriteLine("  " + Path.GetRelativePath(libDir, f));

Console.WriteLine($"\n== {LibraryManifest.FileName} ==");
Console.WriteLine(await File.ReadAllTextAsync(Path.Combine(libDir, LibraryManifest.FileName)));
