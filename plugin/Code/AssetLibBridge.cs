using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Editor;
using Sandbox;

namespace SboxAssetLib.Plugin;

/// <summary>
/// Editor-side of the s&amp;box Asset Library bridge.
///
/// Runs a background loop that:
///   1. writes <c>status.json</c> every ~2s advertising the open project + content path, so the
///      standalone app knows s&amp;box is live and where to install;
///   2. polls the requests folder for import requests the app drops in, compiles the asset, and
///      (for models) spawns it into the open scene, then writes a result file the app waits on.
///
/// Transport is file-based IPC (not sockets) because the editor sandbox blocks HttpListener —
/// the same approach the s&amp;box Claude Bridge uses. The file/JSON plumbing below is plain .NET
/// and is correct as-is; the three ENGINE-CALL helpers at the bottom touch s&amp;box editor APIs
/// that shift between SDK versions — verify them in-editor (the sbox MCP `describe_type` /
/// `search_types` tools confirm current signatures) and adjust if the editor reports errors.
/// </summary>
public static class AssetLibBridge
{
    // Mirror of SboxAssetLib.Core.Bridge.BridgePaths (kept in sync by hand; the plugin can't
    // reference the app's Core.dll inside the s&box sandbox).
    private static readonly string Root =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sbox-asset-lib", "bridge");
    private static string StatusFile => Path.Combine(Root, "status.json");
    private static string RequestsDir => Path.Combine(Root, "requests");
    private static string ResponsesDir => Path.Combine(Root, "responses");

    private static CancellationTokenSource? _cts;
    private static Task? _loop;

    public static bool IsRunning => _loop is { IsCompleted: false };

    [Menu("Editor", "Asset Library/Start Bridge")]
    public static void Start()
    {
        if (IsRunning)
            return;

        Directory.CreateDirectory(RequestsDir);
        Directory.CreateDirectory(ResponsesDir);

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
        Log.Info("[AssetLibBridge] started — listening at " + Root);
    }

    [Menu("Editor", "Asset Library/Stop Bridge")]
    public static void Stop()
    {
        _cts?.Cancel();
        _loop = null;
        try { File.Delete(StatusFile); } catch { }
        Log.Info("[AssetLibBridge] stopped");
    }

    private static async Task RunAsync(CancellationToken ct)
    {
        var lastStatus = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow - lastStatus > TimeSpan.FromSeconds(2))
                {
                    WriteStatus();
                    lastStatus = DateTime.UtcNow;
                }

                foreach (var file in Directory.EnumerateFiles(RequestsDir, "*.json"))
                    ProcessRequest(file);
            }
            catch (Exception ex)
            {
                Log.Warning($"[AssetLibBridge] loop error: {ex.Message}");
            }

            await Task.Delay(500, ct);
        }
    }

    private static void WriteStatus()
    {
        var status = new StatusDto
        {
            Ok = true,
            ProjectName = GetProjectName(),
            ContentPath = GetContentPath(),
            Version = "0.1",
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        WriteJsonAtomic(StatusFile, status);
    }

    private static void ProcessRequest(string requestPath)
    {
        var id = Path.GetFileNameWithoutExtension(requestPath);
        ResultDto result;
        try
        {
            var req = JsonSerializer.Deserialize<RequestDto>(File.ReadAllText(requestPath), JsonOpts)
                      ?? throw new Exception("empty request");

            // Files are already on disk (the app installed them under the content path). Compile,
            // then optionally spawn into the scene. Marshal engine work onto the main thread.
            string? compiled = null;
            Sandbox.Application.DispatchToMainThread(() =>
            {
                compiled = CompileAsset(req.PrimaryAsset);
                if (req.SpawnInScene && string.Equals(req.Kind, "Model", StringComparison.OrdinalIgnoreCase))
                    SpawnInScene(req.PrimaryAsset);
            });

            result = new ResultDto { RequestId = id, Ok = true, CompiledAsset = compiled };
        }
        catch (Exception ex)
        {
            result = new ResultDto { RequestId = id, Ok = false, Error = ex.Message };
        }

        WriteJsonAtomic(Path.Combine(ResponsesDir, id + ".json"), result);
        try { File.Delete(requestPath); } catch { }
    }

    // ===================================================================
    // ENGINE CALLS — verify signatures against the installed SDK in-editor
    // ===================================================================

    /// <summary>The open project's content/addon root (where assets live).</summary>
    private static string? GetContentPath()
    {
        // VERIFY: Project.Current / GetAssetsPath() naming varies by SDK.
        try { return Project.Current?.GetAssetsPath(); }
        catch { return null; }
    }

    private static string? GetProjectName()
    {
        try { return Project.Current?.Config?.Title; }
        catch { return null; }
    }

    /// <summary>Ensure the just-written .vmat/.vmdl is registered + compiled; returns the resource path.</summary>
    private static string? CompileAsset(string addonRelativePath)
    {
        // Assets dropped into the content folder are picked up by the editor's asset watcher.
        // We nudge it explicitly so the import feels instant.
        // VERIFY: AssetSystem.RegisterFile / FindByPath / Compile signatures.
        try
        {
            var content = GetContentPath();
            if (content is not null)
                AssetSystem.RegisterFile(Path.Combine(content, addonRelativePath));

            var asset = AssetSystem.FindByPath(addonRelativePath);
            asset?.Compile(full: false);
            return asset?.Path ?? addonRelativePath;
        }
        catch (Exception ex)
        {
            Log.Warning($"[AssetLibBridge] compile nudge failed (asset may still auto-compile): {ex.Message}");
            return addonRelativePath;
        }
    }

    /// <summary>Spawn a GameObject with the compiled model into the active scene and frame it.</summary>
    private static void SpawnInScene(string vmdlPath)
    {
        // VERIFY: SceneEditorSession.Active.Scene, GameObject creation, ModelRenderer component.
        try
        {
            var scene = SceneEditorSession.Active?.Scene;
            if (scene is null)
            {
                Log.Warning("[AssetLibBridge] no active scene to spawn into.");
                return;
            }

            var go = scene.CreateObject();
            go.Name = Path.GetFileNameWithoutExtension(vmdlPath);
            var renderer = go.Components.GetOrCreate<ModelRenderer>();
            renderer.Model = Model.Load(vmdlPath);

            // Frame the new object so the user sees it immediately.
            SceneEditorSession.Active?.Selection?.Set(go);
        }
        catch (Exception ex)
        {
            Log.Warning($"[AssetLibBridge] spawn failed: {ex.Message}");
        }
    }

    // ---- plumbing ----

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static void WriteJsonAtomic(string path, object value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOpts));
        File.Move(tmp, path, overwrite: true);
    }

    private sealed class StatusDto
    {
        public bool Ok { get; set; }
        public string? ProjectName { get; set; }
        public string? ContentPath { get; set; }
        public string? Version { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class RequestDto
    {
        public string RequestId { get; set; } = "";
        public string PrimaryAsset { get; set; } = "";
        public string Kind { get; set; } = "";
        public bool SpawnInScene { get; set; }
    }

    private sealed class ResultDto
    {
        public string? RequestId { get; set; }
        public bool Ok { get; set; }
        public string? CompiledAsset { get; set; }
        public string? Error { get; set; }
    }
}
