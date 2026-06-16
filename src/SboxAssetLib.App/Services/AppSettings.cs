using System.Text.Json;
using System.Text.Json.Serialization;
using SboxAssetLib.Core.Model;

namespace SboxAssetLib.App.Services;

/// <summary>User-configurable settings, persisted to the platform config dir as JSON.</summary>
public sealed class AppSettings
{
    public string LibraryPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SboxAssetLibrary");

    public string DefaultResolution { get; set; } = "2k";
    public string DefaultImageFormat { get; set; } = "png";
    public string NormalConvention { get; set; } = nameof(Core.Model.NormalConvention.OpenGl);
    public string? BlenderPath { get; set; }
    public string UserAgent { get; set; } = "sbox-asset-lib/0.1 (+https://github.com/sbox-asset-lib)";

    /// <summary>Legacy setting from the pre-file-IPC bridge; kept so old settings files deserialize.</summary>
    public int BridgePort { get; set; } = 28310;

    [JsonIgnore]
    public NormalConvention Normal =>
        Enum.TryParse<NormalConvention>(NormalConvention, out var n) ? n : Core.Model.NormalConvention.OpenGl;

    [JsonIgnore]
    public FormatPrefs Prefs => new()
    {
        ImageFormats = DefaultImageFormat.Equals("jpg", StringComparison.OrdinalIgnoreCase)
            ? ["jpg", "png"]
            : ["png", "jpg"],
        Normal = Normal,
    };

    // ---- persistence ----

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string ConfigPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "sbox-asset-lib");
            return Path.Combine(dir, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(ConfigPath), JsonOpts) ?? new AppSettings();
        }
        catch { /* fall back to defaults on any parse error */ }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
    }
}
