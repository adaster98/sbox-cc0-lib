using System.Text.Json;
using SboxAssetLib.Core.Bridge;

namespace SboxAssetLib.App.Services;

/// <summary>Talks to the s&amp;box editor plugin over file-based IPC (see <see cref="BridgePaths"/>).</summary>
public sealed class SboxBridgeClient
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Returns the plugin status, or null if s&amp;box / the plugin isn't running (stale/missing status).</summary>
    public async Task<BridgeStatus?> PingAsync(CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(BridgePaths.StatusFile))
                return null;
            var json = await File.ReadAllTextAsync(BridgePaths.StatusFile, ct).ConfigureAwait(false);
            var status = JsonSerializer.Deserialize<BridgeStatus>(json, JsonOpts);
            if (status is null)
                return null;
            return DateTimeOffset.UtcNow - status.UpdatedAt <= BridgePaths.StatusMaxAge ? status : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Drops an import request for the plugin and waits for its result.</summary>
    public async Task<ImportResult> ImportAsync(ImportRequest request, CancellationToken ct = default)
    {
        try
        {
            BridgePaths.EnsureDirectories();
            var reqPath = Path.Combine(BridgePaths.RequestsDir, request.RequestId + ".json");
            var respPath = Path.Combine(BridgePaths.ResponsesDir, request.RequestId + ".json");

            // Write atomically so the plugin never reads a half-written request.
            var tmp = reqPath + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(request), ct).ConfigureAwait(false);
            File.Move(tmp, reqPath, overwrite: true);

            // Poll for the response (compiling can take a few seconds).
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(respPath))
                {
                    var json = await File.ReadAllTextAsync(respPath, ct).ConfigureAwait(false);
                    var result = JsonSerializer.Deserialize<ImportResult>(json, JsonOpts);
                    TryDelete(respPath);
                    TryDelete(reqPath);
                    return result ?? new ImportResult { Ok = false, Error = "Malformed response from plugin." };
                }
                await Task.Delay(250, ct).ConfigureAwait(false);
            }

            TryDelete(reqPath);
            return new ImportResult { Ok = false, Error = "Timed out waiting for s&box to compile the asset." };
        }
        catch (Exception ex)
        {
            return new ImportResult { Ok = false, Error = ex.Message };
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
