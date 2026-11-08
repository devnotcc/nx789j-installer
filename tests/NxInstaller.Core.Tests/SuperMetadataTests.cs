using System.IO.Compression;
using System.Security.Cryptography;
using NxInstaller.Core.Edl;
using NxInstaller.Core.Install;
using NxInstaller.Core.Packages;
using Xunit;

namespace NxInstaller.Core.Tests;

/// <summary>
/// data/super_head_a.bin.gz is the first MiB of the Evolution X super image (liblp metadata only,
/// built for slot _a: system_a ... hold the data, the _b partitions are empty).
/// </summary>
public class SuperMetadataTests
{
    const ulong ActiveBit = 1UL << 50;

    static byte[] Head()
    {
        using var gz = new GZipStream(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "data", "super_head_a.bin.gz")), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void BuiltImageIsForSlotA() => Assert.Equal("_a", SuperMetadata.PopulatedSlot(Head()));

    [Fact]
    public void SwapMovesDataToSlotBAndBack()
    {
        var head = Head();
        var b = SuperMetadata.SwapSlots(head);
        Assert.Equal("_b", SuperMetadata.PopulatedSlot(b));
        Assert.Equal(head, SuperMetadata.SwapSlots(b));
        // only names and checksums change: nothing past the metadata copies
        Assert.Equal(head.AsSpan(4096 * 3 + 6 * 65536).ToArray(), b.AsSpan(4096 * 3 + 6 * 65536).ToArray());
    }

    [Fact]
    public void CorruptMetadataIsRefused()
    {
        var head = Head();
        head[4096 * 3 + 300] ^= 1; // inside the first copy's tables
        Assert.Throws<InvalidDataException>(() => SuperMetadata.SwapSlots(head));
    }

    static (PartitionTable, PreparedImage[], string) Setup(string slot, long extra)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"nx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var img = Path.Combine(dir, "super.img");
        using (var f = new FileStream(img, FileMode.Create))
        {
            f.Write(Head());
            f.SetLength(f.Length + extra);
        }
        var size = new FileInfo(img).Length;
        var table = new PartitionTable([
            new(4, "boot_a", 10, 11, slot == "_a" ? ActiveBit : 0), new(4, "boot_b", 12, 13, slot == "_b" ? ActiveBit : 0),
            new(0, "super", 1000, 1999, 0),
        ]);
        var prepared = new[] { new PreparedImage(new PackageImage { Partition = "super", Slot = SlotTarget.None, File = "super.img", Size = size, Sha256 = new string('0', 64) }, img) };
        return (table, prepared, dir);
    }

    [Fact]
    public void PlanOnSlotBRewritesTheStartOfSuper()
    {
        var (table, prepared, dir) = Setup("_b", 8 * Gpt.SectorSize);
        var plan = InstallPlanner.Plan(table, prepared, false, n => InstallPlanner.ZeroFile(dir, n));
        Assert.NotNull(plan.Head);
        Assert.Equal(2, plan.Writes.Count);
        Assert.Equal(prepared[0].LocalFile, plan.Writes[0].File);       // the image first,
        Assert.Equal(plan.Head!.File, plan.Writes[1].File);             // then its start, rewritten
        Assert.Equal("super", plan.Writes[1].Partition.Label);
        Assert.Equal((ulong)SuperMetadata.HeadBytes / Gpt.SectorSize, plan.Writes[1].Sectors);
        Assert.Equal("_b", SuperMetadata.PopulatedSlot(File.ReadAllBytes(plan.Head.File)));

        // what super reads back as afterwards
        var whole = File.ReadAllBytes(prepared[0].LocalFile);
        plan.Head.Bytes.CopyTo(whole, 0);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(whole)),
            SuperMetadata.Sha256WithHead(prepared[0].LocalFile, plan.Head.Bytes, plan.Writes[0].Sectors * Gpt.SectorSize));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void PlanOnSlotAWritesSuperAsBuilt()
    {
        var (table, prepared, dir) = Setup("_a", 8 * Gpt.SectorSize);
        var plan = InstallPlanner.Plan(table, prepared, false, n => InstallPlanner.ZeroFile(dir, n));
        Assert.Null(plan.Head);
        Assert.Single(plan.Writes);
        Directory.Delete(dir, true);
    }
}
