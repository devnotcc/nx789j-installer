using System.Security.Cryptography;
using System.Text.Json;
using ZstdSharp;

namespace NxInstaller.Core.Packages;

/// <summary>
/// Input for building a package: the manifest fields plus, for each image, the local file it is
/// made from. Kept as JSON next to the build scripts (packaging/*.json).
/// </summary>
public sealed record PackageSpec
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required PackageKind Kind { get; init; }
    public string Description { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public string? DisabledReason { get; init; }
    public string? Variant { get; init; }
    public string? Family { get; init; }
    public Compatibility Compat { get; init; } = new();
    public WipePolicy Wipe { get; init; } = new();
    public List<string> Requires { get; init; } = [];
    public List<string> Notes { get; init; } = [];
    public required List<SpecImage> Images { get; init; }
}

public sealed record SpecImage(string Partition, SlotTarget Slot, string Source, string? Option = null);

/// <summary>
/// Builds a publishable package: each image is checksummed, zstd-compressed and cut into parts
/// below GitHub's 2 GiB per-asset limit, and manifest.json lists every part with its checksum.
/// The output folder can be uploaded as a GitHub release as-is, or used directly as a local source.
/// </summary>
public static class PackageBuilder
{
    /// <summary>GitHub release assets must be under 2 GiB; stay well clear of it.</summary>
    public const long PartSize = 1900L << 20;

    public static async Task<PackageManifest> BuildAsync(PackageSpec spec, string outDir, Log log, int level = 9, long partSize = PartSize, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outDir);
        var images = new List<PackageImage>();
        foreach (var img in spec.Images)
        {
            var src = new FileInfo(img.Source);
            if (!src.Exists) throw new FileNotFoundException(img.Source);
            if (src.Length % 4096 != 0) throw new InvalidDataException($"{src.Name}: size is not a multiple of 4096");
            if (Safety.PartitionPolicy.BaseName(img.Partition) != img.Partition)
                throw new InvalidDataException($"{img.Partition}: give the partition without a slot suffix");
            if (!Safety.PartitionPolicy.IsAllowed(img.Partition + (img.Slot == SlotTarget.Active ? "_a" : ""), Safety.WriteKind.Image, out var why))
                throw new InvalidDataException(why); // fail at build time, not on a user's phone

            var file = $"{img.Partition}.img";
            log.Info($"{file}: compressing {src.Length >> 20} MiB");
            var parts = new List<FilePart>();
            string rawSha;
            await using (var input = new FileStream(src.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using var split = new SplitWriter(outDir, file + ".zst", partSize, parts);
                await using (var z = new CompressionStream(split, level, leaveOpen: true))
                {
                    z.SetParameter(ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_nbWorkers, Math.Max(1, Environment.ProcessorCount - 1));
                    var buf = new byte[4 << 20];
                    int n;
                    while ((n = await input.ReadAsync(buf, ct)) > 0)
                    {
                        sha.AppendData(buf, 0, n);
                        await z.WriteAsync(buf.AsMemory(0, n), ct);
                    }
                }
                rawSha = Convert.ToHexStringLower(sha.GetHashAndReset());
            }
            log.Info($"{file}: {parts.Count} part(s), {parts.Sum(p => p.Size) >> 20} MiB compressed");
            images.Add(new PackageImage
            {
                Partition = img.Partition, Slot = img.Slot, File = file, Size = src.Length, Sha256 = rawSha,
                Compression = Compression.Zstd, Parts = parts, Option = img.Option,
            });
        }

        var m = new PackageManifest
        {
            Id = spec.Id, Name = spec.Name, Version = spec.Version, Kind = spec.Kind, Description = spec.Description,
            Enabled = spec.Enabled, DisabledReason = spec.DisabledReason, Variant = spec.Variant, Family = spec.Family,
            Compat = spec.Compat, Wipe = spec.Wipe, Images = images, Requires = spec.Requires, Notes = spec.Notes,
        };
        m.Validate();
        await File.WriteAllTextAsync(Path.Combine(outDir, "manifest.json"), m.ToJson(), ct);
        return m;
    }

    /// <summary>Writes a stream into numbered part files (name.001, name.002, ...), hashing each.</summary>
    sealed class SplitWriter(string dir, string baseName, long partSize, List<FilePart> parts) : Stream
    {
        FileStream? _cur;
        IncrementalHash? _hash;
        long _written;

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> data)
        {
            while (data.Length > 0)
            {
                if (_cur is null) Open();
                var room = (int)Math.Min(partSize - _written, data.Length);
                _cur!.Write(data[..room]);
                _hash!.AppendData(data[..room]);
                _written += room;
                data = data[room..];
                if (_written == partSize) ClosePart();
            }
        }

        void Open()
        {
            var name = $"{baseName}.{parts.Count + 1:000}";
            _cur = new FileStream(Path.Combine(dir, name), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            _written = 0;
        }

        void ClosePart()
        {
            if (_cur is null) return;
            var name = Path.GetFileName(_cur.Name);
            _cur.Dispose();
            parts.Add(new FilePart(name, _written, Convert.ToHexStringLower(_hash!.GetHashAndReset())));
            _hash.Dispose();
            _cur = null;
        }

        protected override void Dispose(bool disposing) { ClosePart(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() { ClosePart(); return base.DisposeAsync(); }
        public override void Flush() => _cur?.Flush();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    public static PackageSpec LoadSpec(string path)
    {
        var spec = JsonSerializer.Deserialize<PackageSpec>(File.ReadAllText(path), PackageManifest.Json)
                   ?? throw new InvalidDataException("empty spec");
        // Image sources are relative to the spec file.
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return spec with { Images = spec.Images.Select(i => i with { Source = Path.GetFullPath(i.Source, dir) }).ToList() };
    }
}
