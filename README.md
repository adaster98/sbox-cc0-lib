# s&box Asset Library

Browse free CC0 textures, models, and HDRIs from places like Poly Haven and ambientCG, then import
them into **s&box** as ready-to-compile Source 2 `.vmat` and `.vmdl` assets. You can also download
them into a reusable library that you mount across projects.

The desktop app is **Proton-aware** on Linux, so it can find and talk to the s&box editor even when
the editor is running through Steam Proton.

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
| cgbookcase | ⏳ Planned | Metadata support exists, but downloads are not wired into the app yet. |
| sharetextures | ⏳ Planned | Not wired into the app yet. |

## How Importing Works

- **Textures**: the app downloads the available PBR maps, converts EXR material inputs to PNG when
  needed, and writes a Source 2 `.vmat` using `shaders/complex.shader`.
- **Models**: the app downloads the mesh and textures, generates a `.vmdl`, and pairs it with a
  generated material so s&box can compile it.
- **s&box import**: the editor bridge writes project status to disk, the app installs files into
  the active project, and the editor picks them up to compile and optionally spawn them.
- **Proton support**: on Linux, the app can locate the bridge inside the s&box Proton prefix and
  translate Wine paths back to native Linux paths automatically.

## Build and Run

Requires the **.NET 10 SDK**.

```bash
dotnet build SboxAssetLib.slnx
dotnet run --project src/SboxAssetLib.App
```

On first run the app creates `~/.config/sbox-asset-lib/settings.json` and caches thumbnails and
downloads under the usual XDG data and cache directories.

## Installing the s&box Editor Library

1. Copy `plugin/` into a library folder inside your s&box project, for example
   `Libraries/adaster98.sbox-asset-lib/`.
2. Keep the bridge code under that library's `Editor/` folder. It uses editor-only APIs and should
   not live under `Code/`.
3. In the editor, run **Editor → Asset Library → Start Bridge**.
4. Start the desktop app. When the status bar shows the current project, use **Import to s&box**.

If the editor is running through Proton, the app will still discover the bridge automatically.

## License

Tooling: your choice. Assets fetched through this app are **CC0** from their respective providers.
The generated library manifest keeps the source and license details with each imported asset.
