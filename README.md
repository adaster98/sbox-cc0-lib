# s&box Asset Library

Browse free CC0 textures, models, and HDRIs from places like Poly Haven and ambientCG, then import
them into **s&box** as ready-to-compile Source 2 `.vmat` and `.vmdl` assets. You can also download
them into a reusable library that you mount across projects. Supports Windows and Linux (Proton Aware)

<img width="1165" height="993" alt="image" src="https://github.com/user-attachments/assets/62e0a248-db84-40d3-b8b2-8c42a59098e6" />

## What It Does

- Browse and search CC0 assets by type.
- Preview categories, tags, and available resolutions before downloading.
- Import straight into the active s&box project through the editor bridge.
- Save assets into a reusable library with generated Source 2 materials and models.

## Providers

| Provider | Status | Notes |
|----------|--------|-------|
| **Poly Haven** | ✅ Working | Textures, models, and HDRIs at all supported resolutions. |
| **ambientCG** | ✅ Working | Material textures at all supported resolutions. |
| **CC0 Textures** | ✅ Working | 4K material texture archives. |
| **TextureCan** | ✅ Working | Material texture archives and model archives. |
| cgbookcase | ⏳ Planned | Metadata support exists, but they have some weird redirect based download which is currently preventing use. |
| sharetextures | ❌ No | Their TOS prevents automating downloads, so I will clone their library at some point. |

## How Importing Works

- **Textures**: the app downloads the available PBR maps, converts EXR material inputs to PNG when
  needed, and writes a Source 2 `.vmat` using `shaders/complex.shader`.
- **Models**: the app downloads the mesh and textures, generates a `.vmdl`, and pairs it with a
  generated material so s&box can compile it.
- **s&box import**: the editor bridge writes project status to disk, the app installs files into
  the active project, and the editor picks them up to compile and optionally spawn them.
- **Proton support**: on Linux, the app can locate the bridge inside the s&box Proton prefix and
  translate Wine paths back to native Linux paths automatically.

## Download
https://github.com/adaster98/sbox-cc0-lib/releases

## Or Compile yourself

Requires the **.NET 10 SDK**.

```bash
dotnet build SboxAssetLib.slnx
dotnet run --project src/SboxAssetLib.App
```

On first run the app creates `~/.config/sbox-asset-lib/settings.json` and caches thumbnails and
downloads under the usual XDG data and cache directories.

## Installing the s&box Editor Library

1. Make `Libraries/adaster98.sbox-asset-lib/` in your s&box project, then copy the contents of `plugin/` into that folder.
2. Restart S&box Editor
3. At the top of the editor, choose **Asset Library → Start Bridge**.
4. Start the desktop app. When the status bar shows the current project, use **Import to s&box**.

If the editor is running through Proton, the app will still discover the bridge automatically.

## License
Assets fetched through this app are **CC0** from their respective providers.
The generated library manifest keeps the source and license details with each imported asset.
