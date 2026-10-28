using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace NxInstaller.Core;

public static class Download
{
    /// <summary>
    /// Downloads to <paramref name="dest"/>, resuming a partial ".part" file when the server allows
    /// it, and checks the SHA-256 when one is given. The file only gets its final name once complete
    /// and verified, so a file with the final name is always a good one.
    /// </summary>
    public static async Task ToFileAsync(HttpClient http, string url, string dest, string? sha256,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (sha256 is not null && File.Exists(dest) && await Sha256FileAsync(dest, ct) == sha256) return;
        var part = dest + ".part";
        var have = File.Exists(part) ? new FileInfo(part).Length : 0;

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (have > 0 && resp.StatusCode != System.Net.HttpStatusCode.PartialContent) have = 0; // no resume support
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength is { } len ? len + have : (long?)null;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var buf = new byte[1 << 20];
            var done = have;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total is > 0) progress?.Report((double)done / total.Value);
            }
        }

        if (sha256 is not null)
        {
            var got = await Sha256FileAsync(part, ct);
            if (got != sha256)
            {
                File.Delete(part);
                throw new InvalidDataException($"{Path.GetFileName(dest)}: checksum mismatch (got {got}, expected {sha256}); the download was deleted");
            }
        }
        File.Move(part, dest, overwrite: true);
    }

    public static async Task<string> Sha256FileAsync(string path, CancellationToken ct = default)
    {
        await using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct));
    }
}
