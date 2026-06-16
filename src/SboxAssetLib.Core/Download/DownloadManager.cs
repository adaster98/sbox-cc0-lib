using System.Security.Cryptography;
using SboxAssetLib.Core.Model;

namespace SboxAssetLib.Core.Download;

public readonly record struct DownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double? Fraction => TotalBytes is > 0 ? (double)BytesReceived / TotalBytes!.Value : null;
}

/// <summary>A downloaded file paired with where it landed on disk.</summary>
public sealed record FetchedFile(DownloadFile File, string LocalPath);

/// <summary>
/// Downloads provider files to disk with content-addressed caching: a file is only
/// re-downloaded if it is missing or fails md5/size validation, so re-imports are instant.
/// </summary>
public sealed class DownloadManager
{
    private readonly HttpClient _http;

    public DownloadManager(HttpClient http) => _http = http;

    /// <summary>Download a single file into <paramref name="destDir"/>, returning its absolute path.</summary>
    public async Task<string> DownloadAsync(
        DownloadFile file, string destDir, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        // FileName may carry a relative subpath for mesh dependencies (e.g. "textures/foo.png").
        var rel = file.FileName.Replace('\\', '/');
        var destPath = Path.GetFullPath(Path.Combine(destDir, rel));
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        if (File.Exists(destPath) && await IsValidAsync(destPath, file.Md5, file.Size, ct).ConfigureAwait(false))
        {
            progress?.Report(new DownloadProgress(new FileInfo(destPath).Length, file.Size > 0 ? file.Size : null));
            return destPath;
        }

        var tmp = destPath + ".part";
        using (var resp = await _http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? (file.Size > 0 ? file.Size : null);
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

            var buffer = new byte[1 << 16];
            long received = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;
                progress?.Report(new DownloadProgress(received, total));
            }
        }

        if (file.Md5 is { Length: > 0 } md5 && !string.Equals(await Md5HexAsync(tmp, ct).ConfigureAwait(false), md5, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(tmp);
            throw new InvalidDataException($"MD5 mismatch for {file.FileName} (expected {md5}).");
        }

        File.Move(tmp, destPath, overwrite: true);
        return destPath;
    }

    /// <summary>Download every file in the set, preserving relative subpaths under <paramref name="destDir"/>.</summary>
    public async Task<IReadOnlyList<FetchedFile>> DownloadAllAsync(
        IEnumerable<DownloadFile> files, string destDir, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var result = new List<FetchedFile>();
        foreach (var f in files)
            result.Add(new FetchedFile(f, await DownloadAsync(f, destDir, progress, ct).ConfigureAwait(false)));
        return result;
    }

    private static async Task<bool> IsValidAsync(string path, string? md5, long size, CancellationToken ct)
    {
        if (md5 is { Length: > 0 })
            return string.Equals(await Md5HexAsync(path, ct).ConfigureAwait(false), md5, StringComparison.OrdinalIgnoreCase);
        if (size > 0)
            return new FileInfo(path).Length == size;
        return true; // nothing to validate against — assume the cached copy is good
    }

    private static async Task<string> Md5HexAsync(string path, CancellationToken ct)
    {
        await using var s = File.OpenRead(path);
        var hash = await MD5.HashDataAsync(s, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
