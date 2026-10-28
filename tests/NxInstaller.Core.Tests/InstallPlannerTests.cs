using NxInstaller.Core.Edl;
using NxInstaller.Core.Install;
using NxInstaller.Core.Packages;
using NxInstaller.Core.Safety;
using Xunit;

namespace NxInstaller.Core.Tests;

public class InstallPlannerTests
{
    const int Sector = 4096;
    const ulong ActiveBit = 1UL << 50;

    static PartitionTable Table(string activeSlot)
    {
        var parts = new List<GptPartition>
        {
            new(0, "super", 1000, 1000 + 100 - 1, 0),            // 100 sectors
            new(0, "metadata", 2000, 2000 + 50 - 1, 0),
            new(0, "userdata", 3000, 3000 + 10000 - 1, 0),
            new(4, "boot_a", 10, 11, activeSlot == "_a" ? ActiveBit : 0),
            new(4, "boot_b", 12, 13, activeSlot == "_b" ? ActiveBit : 0),
            new(4, "vbmeta_system_a", 20, 20, activeSlot == "_a" ? ActiveBit : 0),
            new(4, "vbmeta_system_b", 21, 21, 0),
        };
        return new PartitionTable(parts);
    }

    static string MakeImage(long bytes)
    {
        var p = Path.Combine(Path.GetTempPath(), $"nx-{Guid.NewGuid():N}.img");
        using var f = new FileStream(p, FileMode.Create);
        f.SetLength(bytes);
        return p;
    }

    static string Zero(long bytes) => InstallPlanner.ZeroFile(Path.GetTempPath(), bytes);

    [Theory]
    [InlineData("_a")]
    [InlineData("_b")]
    public void FindsActiveSlot(string slot) =>
        Assert.Equal(slot, InstallPlanner.ActiveSlot(Table(slot)));

    [Fact]
    public void BothSlotsActiveIsRejected()
    {
        var t = new PartitionTable([
            new(4, "boot_a", 10, 11, ActiveBit),
            new(4, "boot_b", 12, 13, ActiveBit),
        ]);
        Assert.Throws<InvalidDataException>(() => InstallPlanner.ActiveSlot(t));
    }

    [Fact]
    public void PlanTargetsActiveSlotAndRightOffsets()
    {
        var table = Table("_a");
        var img = MakeImage(5 * Sector);
        var prepared = new[] { new PreparedImage(new PackageImage { Partition = "vbmeta_system", File = "v.img", Size = 5 * Sector, Sha256 = new string('0', 64) }, img) };
        // vbmeta_system_a is only 1 sector in the table; use a fitting target instead
        table = new PartitionTable([
            new(4, "boot_a", 10, 11, ActiveBit), new(4, "boot_b", 12, 13, 0),
            new(0, "super", 1000, 1099, 0),
            new(4, "vbmeta_system_a", 20, 29, ActiveBit), new(4, "vbmeta_system_b", 30, 39, 0),
            new(0, "metadata", 2000, 2049, 0), new(0, "userdata", 3000, 3099, 0),
        ]);
        var plan = InstallPlanner.Plan(table, prepared, wipe: false, Zero);
        var w = Assert.Single(plan.Writes);
        Assert.Equal("vbmeta_system_a", w.Partition.Label); // active slot
        Assert.Equal(20u, w.Partition.FirstSector);
        Assert.Equal(5u, w.Sectors);
        Assert.Empty(plan.Wipes);
        File.Delete(img);
    }

    [Fact]
    public void RefusesImageLargerThanPartition()
    {
        var table = Table("_a");
        var img = MakeImage(200 * Sector); // super is 100 sectors
        var prepared = new[] { new PreparedImage(new PackageImage { Partition = "super", Slot = SlotTarget.None, File = "s.img", Size = 200 * Sector, Sha256 = new string('0', 64) }, img) };
        Assert.Throws<InvalidDataException>(() => InstallPlanner.Plan(table, prepared, false, Zero));
        File.Delete(img);
    }

    [Fact]
    public void RefusesSizeMismatchWithManifest()
    {
        var table = Table("_a");
        var img = MakeImage(10 * Sector);
        var prepared = new[] { new PreparedImage(new PackageImage { Partition = "super", Slot = SlotTarget.None, File = "s.img", Size = 9 * Sector, Sha256 = new string('0', 64) }, img) };
        Assert.Throws<InvalidDataException>(() => InstallPlanner.Plan(table, prepared, false, Zero));
        File.Delete(img);
    }

    [Fact]
    public void WipeAddsMetadataAndUserdataZeros()
    {
        var table = Table("_a");
        var plan = InstallPlanner.Plan(table, [], wipe: true, Zero);
        Assert.Equal(2, plan.Wipes.Count);
        Assert.Contains(plan.Wipes, w => w.Partition.Label == "metadata");
        Assert.Contains(plan.Wipes, w => w.Partition.Label == "userdata");
        Assert.All(plan.Wipes, w => Assert.Equal(WriteKind.Wipe, w.Kind));
        // metadata is only 50 sectors (< 64 MiB), so the wipe is clamped to the partition size
        Assert.Equal(50u, plan.Wipes.First(w => w.Partition.Label == "metadata").Sectors);
    }

    [Theory]
    [InlineData(true, "evolutionx", true)]    // always-wipe package
    [InlineData(false, null, true)]            // unknown current OS
    [InlineData(false, "evolutionx", false)]   // same family, no wipe
    [InlineData(false, "lineageos", true)]     // different family
    public void NeedsWipeFollowsManifestRule(bool always, string? current, bool expected)
    {
        var m = new PackageManifest
        {
            Id = "x", Name = "X", Version = "1", Kind = PackageKind.Android,
            Compat = new Compatibility(), Images = [],
            Wipe = new WipePolicy { Always = always, UnlessFamily = ["evolutionx"] },
        };
        Assert.Equal(expected, InstallPlanner.NeedsWipe(m, current));
    }
}
