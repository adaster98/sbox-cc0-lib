using System.Security.Cryptography;
using System.Text;

namespace SboxAssetLib.App.Services;

/// <summary>Downloads and disk-caches preview thumbnails so the gallery scrolls fast.</summary>
public sealed class ThumbnailCache
{
    private readonly HttpClient _http;
    private readonly string _dir;
    private readonly SemaphoreSlim _gate = new(8); // limit concurrent thumbnail fetches

    public ThumbnailCache(HttpClient http)
    {
        _http = http;
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "sbox-asset-lib", "thumbs");
        Directory.CreateDirectory(_dir);
    }

    /// <summary>Returns a local file path for the thumbnail, downloading it once if needed.</summary>
    public async Task<string?> GetLocalPathAsync(string? url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var ext = Path.GetExtension(new Uri(url).AbsolutePath);
        if (string.IsNullOrEmpty(ext) || ext.Length > 5)
            ext = ".jpg";
        var path = Path.Combine(_dir, Hash(url) + ext);
        if (File.Exists(path) && new FileInfo(path).Length > 0)
            return path;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0)
                return path;
            var bytes = await _http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
            var tmp = path + ".part";
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
            return path;
        }
        catch
        {
            return null; // a missing thumbnail shouldn't break the gallery
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Hash(string s) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(s)));
}
