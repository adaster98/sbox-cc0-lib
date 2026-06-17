namespace SboxAssetLib.Core.Bridge;

/// <summary>
/// File-based IPC contract between the standalone app and the s&amp;box editor plugin.
///
/// We use files (not sockets) because the s&amp;box editor sandbox blocks raw <c>HttpListener</c>
/// from addon code — this is the same pattern the existing s&amp;box "Claude Bridge" uses.
///
/// Flow:
///   • The plugin writes <see cref="BridgePaths.StatusFile"/> every couple of seconds with the
///     open project's content path. The app reads it to know s&amp;box is live and where to install.
///   • The app installs the asset's files directly under that content path (reusing AssetInstaller),
///     then drops an <see cref="ImportRequest"/> JSON into <see cref="BridgePaths.RequestsDir"/>.
///   • The plugin picks it up, compiles the asset (and optionally spawns it), then writes an
///     <see cref="ImportResult"/> into <see cref="BridgePaths.ResponsesDir"/> keyed by request id.
/// </summary>
public static class BridgePaths
{
    /// <summary>Shared bridge directory under the user's local app data (same on both sides).</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "sbox-asset-lib", "bridge");

    public static string StatusFile => Path.Combine(Root, "status.json");
    public static string RequestsDir => Path.Combine(Root, "requests");
    public static string ResponsesDir => Path.Combine(Root, "responses");

    /// <summary>A status file older than this means s&amp;box / the plugin is not running.</summary>
    public static readonly TimeSpan StatusMaxAge = TimeSpan.FromSeconds(12);

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(RequestsDir);
        Directory.CreateDirectory(ResponsesDir);
    }
}

public sealed record BridgeStatus
{
    public bool Ok { get; init; } = true;
    public string? ProjectName { get; init; }

    /// <summary>Absolute path of the open project's content/addon root to install into.</summary>
    public string? ContentPath { get; init; }

    public string? Version { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ImportRequest
{
    public required string RequestId { get; init; }

    /// <summary>Addon-relative path of the compilable asset, e.g. "materials/wood/oak/oak.vmat".</summary>
    public required string PrimaryAsset { get; init; }

    /// <summary>"Texture" or "Model".</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// All addon-relative files written for this asset (source textures, mesh, material).
    /// The bridge registers + compiles the source textures <em>before</em> the primary asset so a
    /// material's generated child vtex exist on first compile. Without this the bridge compiles the
    /// <c>.vmat</c> before its texture children are known to the AssetSystem, leaving the material
    /// with "missing children" — red checkers until a manual editor save, plus on-demand recompile spam.
    /// </summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>For models: spawn a GameObject in the open scene and frame it.</summary>
    public bool SpawnInScene { get; init; }
}

public sealed record ImportResult
{
    public string? RequestId { get; init; }
    public bool Ok { get; init; }
    public string? CompiledAsset { get; init; }
    public string? Error { get; init; }
}
