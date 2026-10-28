using NxInstaller.Core.Packages;
using Xunit;

namespace NxInstaller.Core.Tests;

public class PackageBuilderTests
{
    [Fact]
    public async Task BuildThenAssembleRoundTrips()
    {
        var root = Path.Combine(Path.GetTempPath(), $"nx-pack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            // 64 KiB of random data: incompressible, so a 16 KiB part size gives several parts.
            var raw = new byte[64 * 1024];
            Random.Shared.NextBytes(raw);
            var src = Path.Combine(root, "boot.img");
            await File.WriteAllBytesAsync(src, raw);

            var spec = new PackageSpec
            {
                Id = "test", Name = "Test", Version = "1", Kind = PackageKind.Android,
                Images = [new SpecImage("boot", SlotTarget.Active, src)],
            };
            var outDir = Path.Combine(root, "pkg");
            using var log = new Log(Path.Combine(root, "logs"));
            var m = await PackageBuilder.BuildAsync(spec, outDir, log, level: 1, partSize: 16 * 1024);

            var img = Assert.Single(m.Images);
            Assert.True(img.Parts.Count > 1);
            Assert.Equal(raw.Length, img.Size);

            // The installer side: list the folder, then join + decompress + verify.
            var entry = Assert.Single(await new FolderSource(outDir).ListAsync(default));
            var built = await ImageAssembler.AssembleAsync(entry, entry.Manifest.Images[0], Path.Combine(root, "dl"), Path.Combine(root, "work"), log, null, default);
            Assert.Equal(raw, await File.ReadAllBytesAsync(built));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RefusesFirmwarePartitionAtBuildTime()
    {
        var root = Path.Combine(Path.GetTempPath(), $"nx-pack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var src = Path.Combine(root, "abl.img");
            await File.WriteAllBytesAsync(src, new byte[4096]);
            var spec = new PackageSpec
            {
                Id = "bad", Name = "Bad", Version = "1", Kind = PackageKind.Android,
                Images = [new SpecImage("abl", SlotTarget.Active, src)],
            };
            using var log = new Log(Path.Combine(root, "logs"));
            await Assert.ThrowsAsync<InvalidDataException>(() => PackageBuilder.BuildAsync(spec, Path.Combine(root, "o"), log));
        }
        finally { Directory.Delete(root, true); }
    }
}
