using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ZstdSharp;

namespace NxInstaller.Core.Packages;

/// <summary>A package found in a source, with where to get its files.</summary>
public sealed record PackageEntry(PackageManifest Manifest, IPackageSource Source, string Location);

public interface IPackageSource
{
    string Name { get; }
    Task<List<PackageEntry>> ListAsync(CancellationToken ct);
    /// <summary>Makes the named file (a release asset / a file in the folder) available locally.</summary>
    Task<string> FetchAsync(PackageEntry entry, FilePart part, string destDir, IProgress<double>? progress, CancellationToken ct);
}

/// <summary>
/// Packages published as GitHub releases: each release carries a manifest.json and the image
/// parts. Large images are split below GitHub's 2 GiB per-file limit and joined after download.
/// </summary>
public sealed class GitHubReleaseSource(HttpClient http, string repository) : IPackageSource
{
    public string Name => $"GitHub {repository}";

    public async Task<List<PackageEntry>> ListAsync(CancellationToken ct)
    {
        var releases = await http.GetFromJsonAsync<List<GhRelease>>($"https://api.github.com/repos/{repository}/releases?per_page=50", ct) ?? [];
        var list = new List<PackageEntry>();
        foreach (var r in releases.Where(r => !r.Draft))
        {
            var asset = r.Assets.FirstOrDefault(a => a.Name == "manifest.json");
            if (asset is null) continue;
            try
            {
                var json = await http.GetStringAsync(asset.BrowserDownloadUrl, ct);
                list.Add(new PackageEntry(PackageManifest.Load(json), this, r.TagName));
            }
            catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException)
            {
                // a broken or too-new release is skipped, not fatal for the whole list
            }
        }
        return list;
    }

    public async Task<string> FetchAsync(PackageEntry entry, FilePart part, string destDir, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, part.Name);
        var url = $"https://github.com/{repository}/releases/download/{Uri.EscapeDataString(entry.Location)}/{Uri.EscapeDataString(part.Name)}";
        await Download.ToFileAsync(http, url, dest, part.Sha256, progress, ct);
        return dest;
    }

    sealed record GhRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("assets")] List<GhAsset> Assets);

    sealed record GhAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);
}

/// <summary>Packages in a local folder or USB drive: one sub-folder per package with its manifest.json.</summary>
public sealed class FolderSource(string root) : IPackageSource
{
    public string Name => $"Folder {root}";

    public Task<List<PackageEntry>> ListAsync(CancellationToken ct)
    {
        var list = new List<PackageEntry>();
        if (!Directory.Exists(root)) return Task.FromResult(list);
        foreach (var m in Directory.EnumerateFiles(root, "manifest.json", SearchOption.AllDirectories))
        {
            try { list.Add(new PackageEntry(PackageManifest.Load(File.ReadAllText(m)), this, Path.GetDirectoryName(m)!)); }
            catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException) { }
        }
        return Task.FromResult(list);
    }

    public async Task<string> FetchAsync(PackageEntry entry, FilePart part, string destDir, IProgress<double>? progress, CancellationToken ct)
    {
        var path = Path.Combine(entry.Location, part.Name);
        if (!File.Exists(path)) throw new FileNotFoundException($"{part.Name} is missing from {entry.Location}");
        if (new FileInfo(path).Length != part.Size || await Download.Sha256FileAsync(path, ct) != part.Sha256)
            throw new InvalidDataException($"{part.Name} in {entry.Location} does not match its checksum");
        progress?.Report(1);
        return path;
    }
}

/// <summary>Turns downloaded parts into the final raw image and verifies it.</summary>
public static class ImageAssembler
{
    public static async Task<string> AssembleAsync(PackageEntry entry, PackageImage image, string downloadDir, string outDir,
        Log log, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(outDir);
        var final = Path.Combine(outDir, image.File);
        if (File.Exists(final) && new FileInfo(final).Length == image.Size && await Download.Sha256FileAsync(final, ct) == image.Sha256)
        {
            log.Info($"{image.File}: already prepared");
            return final;
        }

        var parts = image.Parts.Count > 0 ? image.Parts : [new FilePart(image.File, image.Size, image.Sha256)];
        var local = new List<string>();
        for (var i = 0; i < parts.Count; i++)
        {
            var idx = i;
            log.Info($"getting {parts[i].Name} ({parts[i].Size / (1 << 20)} MiB)");
            local.Add(await entry.Source.FetchAsync(entry, parts[i], downloadDir,
                progress is null ? null : new Progress<double>(f => progress.Report((idx + f) / parts.Count * 0.8)), ct));
        }

        log.Info($"{image.File}: joining {local.Count} part(s){(image.Compression == Compression.Zstd ? " and decompressing" : "")}");
        var tmp = final + ".tmp";
        await using (var joined = new ConcatStream(local))
        await using (var outStream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            Stream src = image.Compression == Compression.Zstd ? new DecompressionStream(joined) : joined;
            await using (src) await src.CopyToAsync(outStream, 1 << 20, ct);
        }
        progress?.Report(0.9);
        if (new FileInfo(tmp).Length != image.Size || await Download.Sha256FileAsync(tmp, ct) != image.Sha256)
        {
            File.Delete(tmp);
            throw new InvalidDataException($"{image.File}: assembled image does not match its checksum");
        }
        File.Move(tmp, final, overwrite: true);
        progress?.Report(1);
        return final;
    }
}

/// <summary>Reads several files back to back as one stream.</summary>
sealed class ConcatStream(IReadOnlyList<string> files) : Stream
{
    int _index = -1;
    FileStream? _cur;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        while (true)
        {
            if (_cur is null)
            {
                if (++_index >= files.Count) return 0;
                _cur = new FileStream(files[_index], FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            }
            var n = _cur.Read(buffer);
            if (n > 0) return n;
            _cur.Dispose();
            _cur = null;
        }
    }

    protected override void Dispose(bool disposing) { _cur?.Dispose(); base.Dispose(disposing); }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
