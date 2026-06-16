using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using SboxAssetLib.Core.Bridge;

namespace SboxAssetLib.App.Services;

public enum SboxBridgeConnectionKind
{
    Native,
    Proton,
    Manual
}

public sealed record SboxBridgeConnection
{
    public required SboxBridgeConnectionKind Kind { get; init; }
    public required string Root { get; init; }
    public required BridgeStatus Status { get; init; }
    public string? ProtonPrefix { get; init; }
    public string? NativeContentPath { get; init; }
    public string? ContentPathError { get; init; }

    public string StatusFile => Path.Combine(Root, "status.json");
    public string RequestsDir => Path.Combine(Root, "requests");
    public string ResponsesDir => Path.Combine(Root, "responses");
    public bool CanReachContentPath => !string.IsNullOrWhiteSpace(NativeContentPath);

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RequestsDir);
        Directory.CreateDirectory(ResponsesDir);
    }
}

/// <summary>Talks to the s&amp;box editor plugin over file-based IPC.</summary>
public sealed class SboxBridgeClient
{
    private const string BridgeDirName = "sbox-asset-lib";
    private const string BridgeChildDirName = "bridge";
    private const string SboxEditorAppId = "2129370";
    private const string SboxGameAppId = "590830";

    private static readonly string[] PreferredAppIds = [SboxEditorAppId, SboxGameAppId];
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly Regex VdfPair = new("\"(?<key>(?:\\\\.|[^\"])*)\"\\s*\"(?<value>(?:\\\\.|[^\"])*)\"", RegexOptions.Compiled);
    private static readonly Regex WindowsDrivePath = new(@"^(?<drive>[a-zA-Z]):[\\/]*(?<tail>.*)$", RegexOptions.Compiled);

    private SboxBridgeConnection? _lastConnection;

    /// <summary>Returns the live bridge connection, or null if s&amp;box / the plugin isn't running.</summary>
    public async Task<SboxBridgeConnection?> PingAsync(CancellationToken ct = default)
    {
        foreach (var root in EnumerateBridgeRoots())
        {
            var connection = await TryReadConnectionAsync(root.Root, root.Kind, root.ProtonPrefix, ct).ConfigureAwait(false);
            if (connection is null)
                continue;

            _lastConnection = connection;
            return connection;
        }

        _lastConnection = null;
        return null;
    }

    /// <summary>Drops an import request for the plugin and waits for its result.</summary>
    public async Task<ImportResult> ImportAsync(SboxBridgeConnection connection, ImportRequest request, CancellationToken ct = default)
    {
        try
        {
            connection.EnsureDirectories();
            var reqPath = Path.Combine(connection.RequestsDir, request.RequestId + ".json");
            var respPath = Path.Combine(connection.ResponsesDir, request.RequestId + ".json");

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

    private async Task<SboxBridgeConnection?> TryReadConnectionAsync(
        string root,
        SboxBridgeConnectionKind kind,
        string? protonPrefix,
        CancellationToken ct)
    {
        try
        {
            var statusPath = Path.Combine(root, "status.json");
            if (!File.Exists(statusPath))
                return null;

            var json = await File.ReadAllTextAsync(statusPath, ct).ConfigureAwait(false);
            var status = JsonSerializer.Deserialize<BridgeStatus>(json, JsonOpts);
            if (status is null || DateTimeOffset.UtcNow - status.UpdatedAt > BridgePaths.StatusMaxAge)
                return null;

            var (nativeContentPath, contentPathError) = ResolveContentPath(status.ContentPath, protonPrefix);
            return new SboxBridgeConnection
            {
                Kind = kind,
                Root = root,
                Status = status,
                ProtonPrefix = protonPrefix,
                NativeContentPath = nativeContentPath,
                ContentPathError = contentPathError
            };
        }
        catch
        {
            return null;
        }
    }

    private IEnumerable<BridgeRootCandidate> EnumerateBridgeRoots()
    {
        var yielded = new HashSet<string>(StringComparer.Ordinal);

        if (TryAdd(BridgePaths.Root, yielded))
            yield return new BridgeRootCandidate(BridgePaths.Root, SboxBridgeConnectionKind.Native, null);

        if (_lastConnection is { } cached && TryAdd(cached.Root, yielded))
            yield return new BridgeRootCandidate(cached.Root, cached.Kind, cached.ProtonPrefix);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            yield break;

        foreach (var prefix in DiscoverProtonPrefixes())
        {
            foreach (var root in EnumeratePrefixBridgeRoots(prefix))
            {
                if (TryAdd(root, yielded))
                    yield return new BridgeRootCandidate(root, SboxBridgeConnectionKind.Proton, prefix);
            }
        }
    }

    private static IEnumerable<string> DiscoverProtonPrefixes()
    {
        var steamRoots = DiscoverSteamRoots().ToArray();
        foreach (var appId in PreferredAppIds)
        {
            foreach (var steamRoot in steamRoots)
            {
                var prefix = Path.Combine(steamRoot, "steamapps", "compatdata", appId, "pfx");
                if (Directory.Exists(prefix))
                    yield return Path.GetFullPath(prefix);
            }
        }
    }

    private static IEnumerable<string> DiscoverSteamRoots()
    {
        var roots = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in EnumerateStandardSteamRootCandidates())
        {
            if (Directory.Exists(candidate))
                AddSteamRoot(candidate, roots);
        }

        foreach (var root in roots.ToArray())
        {
            var libraryFile = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            foreach (var libraryRoot in ReadSteamLibraryRoots(libraryFile))
                AddSteamRoot(libraryRoot, roots);
        }

        return roots;
    }

    private static IEnumerable<string> EnumerateStandardSteamRootCandidates()
    {
        foreach (var envName in new[] { "STEAM_DIR", "STEAM_ROOT", "STEAM_COMPAT_CLIENT_INSTALL_PATH" })
        {
            var value = Environment.GetEnvironmentVariable(envName);
            if (!string.IsNullOrWhiteSpace(value))
                yield return value;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            yield break;

        var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(xdgData))
            xdgData = Path.Combine(home, ".local", "share");

        yield return Path.Combine(home, ".steam", "steam");
        yield return Path.Combine(home, ".steam", "root");
        yield return Path.Combine(xdgData, "Steam");

        // Flatpak Steam keeps its config under the user's Flatpak app data.
        var flatpakData = Path.Combine(home, ".var", "app", "com.valvesoftware.Steam");
        yield return Path.Combine(flatpakData, ".steam", "steam");
        yield return Path.Combine(flatpakData, ".local", "share", "Steam");
    }

    private static IEnumerable<string> ReadSteamLibraryRoots(string libraryFoldersPath)
    {
        if (!File.Exists(libraryFoldersPath))
            yield break;

        foreach (Match match in VdfPair.Matches(File.ReadAllText(libraryFoldersPath)))
        {
            if (!string.Equals(UnescapeVdf(match.Groups["key"].Value), "path", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = UnescapeVdf(match.Groups["value"].Value);
            if (!string.IsNullOrWhiteSpace(value))
                yield return value;
        }
    }

    private static IEnumerable<string> EnumeratePrefixBridgeRoots(string protonPrefix)
    {
        var usersDir = Path.Combine(protonPrefix, "drive_c", "users");
        if (!Directory.Exists(usersDir))
            yield break;

        foreach (var userDir in Directory.EnumerateDirectories(usersDir))
        {
            var root = Path.Combine(userDir, "AppData", "Local", BridgeDirName, BridgeChildDirName);
            if (Directory.Exists(root) || File.Exists(Path.Combine(root, "status.json")))
                yield return root;
        }
    }

    private static (string? NativePath, string? Error) ResolveContentPath(string? contentPath, string? protonPrefix)
    {
        if (string.IsNullOrWhiteSpace(contentPath))
            return (null, "The bridge did not report an open project asset path.");

        var nativePath = protonPrefix is null
            ? NormalizeNativePath(contentPath)
            : WinePathTranslator.TryTranslate(protonPrefix, contentPath) ?? NormalizeNativePath(contentPath);

        if (nativePath is null)
            return (null, $"s&box reported '{contentPath}', but it could not be translated to a Linux path.");

        if (!Directory.Exists(nativePath))
            return (null, $"s&box reported '{contentPath}', translated to '{nativePath}', but that folder is not reachable from Linux.");

        return (nativePath, null);
    }

    private static string? NormalizeNativePath(string path)
    {
        if (!Path.IsPathRooted(path))
            return null;

        try { return Path.GetFullPath(path); }
        catch { return null; }
    }

    private static void AddSteamRoot(string path, HashSet<string> roots)
    {
        try
        {
            roots.Add(Path.GetFullPath(path));
        }
        catch { /* ignore malformed env/config paths */ }
    }

    private static bool TryAdd(string path, HashSet<string> yielded)
    {
        try { return yielded.Add(Path.GetFullPath(path)); }
        catch { return false; }
    }

    private static string UnescapeVdf(string value) =>
        value.Replace("\\\\", "\\", StringComparison.Ordinal).Replace("\\\"", "\"", StringComparison.Ordinal);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    private readonly record struct BridgeRootCandidate(string Root, SboxBridgeConnectionKind Kind, string? ProtonPrefix);

    private static class WinePathTranslator
    {
        public static string? TryTranslate(string protonPrefix, string windowsPath)
        {
            var match = WindowsDrivePath.Match(windowsPath);
            if (!match.Success)
                return null;

            var drive = match.Groups["drive"].Value.ToLowerInvariant();
            var driveRoot = ResolveDriveRoot(protonPrefix, drive);
            if (driveRoot is null)
                return null;

            var tail = match.Groups["tail"].Value
                .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

            try
            {
                return Path.GetFullPath(tail.Length == 0
                    ? driveRoot
                    : Path.Combine([driveRoot, .. tail]));
            }
            catch
            {
                return null;
            }
        }

        private static string? ResolveDriveRoot(string protonPrefix, string drive)
        {
            var linkPath = Path.Combine(protonPrefix, "dosdevices", drive + ":");
            try
            {
                var target = File.ResolveLinkTarget(linkPath, returnFinalTarget: true);
                if (target is null)
                    return Directory.Exists(linkPath) ? Path.GetFullPath(linkPath) : null;

                var targetPath = target.FullName;
                if (!Path.IsPathRooted(targetPath))
                    targetPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(linkPath)!, targetPath));

                return targetPath;
            }
            catch
            {
                return null;
            }
        }
    }
}
