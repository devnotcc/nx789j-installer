using System.Buffers.Binary;
using NxInstaller.Core.Edl;
using Xunit;

namespace NxInstaller.Core.Tests;

/// <summary>
/// Uses the real LUN 4 partition tables of the test phone (primary: sectors 0-5, backup: the last
/// 5 sectors), copied from its 2026-10-06 backup. They hold device-specific GUIDs, so they stay
/// local (see .gitignore) and these tests skip when the files are absent.
/// </summary>
public class GptRepairTests
{
    const int S = Gpt.SectorSize;
    const ulong Mark = 1UL << GptRepair.UnbootableBit;
    static string Data(string f) => Path.Combine(AppContext.BaseDirectory, "data", f);
    static bool Have => File.Exists(Data("lun4-gpt-primary.bin")) && File.Exists(Data("lun4-gpt-backup.bin"));

    static (byte[] buf, GptRepair.Table t) Primary()
    {
        var b = File.ReadAllBytes(Data("lun4-gpt-primary.bin"));
        return (b, GptRepair.Locate(b, 1, 0));
    }

    static (byte[] buf, GptRepair.Table t, ulong start) Backup()
    {
        var (_, pt) = Primary();
        var start = pt.AlternateLba - 4;
        var b = File.ReadAllBytes(Data("lun4-gpt-backup.bin"));
        return (b, GptRepair.Locate(b, 4, start), start);
    }

    /// <summary>Marks the entry unbootable the way a failed boot does, with valid CRCs.</summary>
    static byte[] Mark55(byte[] buf, GptRepair.Table t, string label)
    {
        var d = (byte[])buf.Clone();
        for (var i = 0; i < t.Count; i++)
        {
            var o = t.EntriesOffset + i * t.EntrySize;
            if (System.Text.Encoding.Unicode.GetString(d, o + 56, 72).TrimEnd('\0') != label) continue;
            var a = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(o + 48));
            BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(o + 48), a | Mark);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(t.HeaderOffset + 88), Crc(d.AsSpan(t.EntriesOffset, t.Count * t.EntrySize)));
        var hs = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(t.HeaderOffset + 12));
        d.AsSpan(t.HeaderOffset + 16, 4).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(t.HeaderOffset + 16), Crc(d.AsSpan(t.HeaderOffset, hs)));
        return d;
    }

    static uint Crc(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) { c ^= b; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; }
        return c ^ 0xFFFFFFFFu;
    }

    [Fact]
    public void RealTablesParseAndPointAtEachOther()
    {
        if (!Have) return; // local-only test data
        var (_, pt) = Primary();
        var (_, bt, start) = Backup();
        Assert.Equal(1048575UL, pt.AlternateLba);   // LUN 4 has 1048576 sectors of 4 KiB
        Assert.Equal(1UL, bt.AlternateLba);
        Assert.Equal(start, bt.EntriesLba);          // backup entries sit right before the backup header
    }

    [Fact]
    public void HealthyRealTableHasNothingToRepair()
    {
        if (!Have) return;
        var (b, t) = Primary();
        Assert.Equal(0UL, GptRepair.Attributes(b, t, "boot_a")!.Value & Mark);
        Assert.Throws<InvalidOperationException>(() => GptRepair.ClearUnbootable(b, t, "boot_a"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RepairRestoresTheOriginalTableExactly(bool primaryCopy)
    {
        if (!Have) return;
        byte[] orig; GptRepair.Table t; ulong start;
        if (primaryCopy) { (orig, t) = Primary(); start = 0; }
        else (orig, t, start) = Backup();

        var broken = Mark55(orig, t, "boot_a");
        var bt = GptRepair.Locate(broken, t.HeaderOffset / S, start);
        Assert.NotEqual(0UL, GptRepair.Attributes(broken, bt, "boot_a")!.Value & Mark);

        var repaired = GptRepair.ClearUnbootable(broken, bt, "boot_a");
        // clearing the bit gives back the byte-identical table the phone had before
        Assert.Equal(orig, repaired);
        // and the changes vs the broken table are only the attribute byte and the two CRCs (<= 9 bytes)
        Assert.InRange(broken.Zip(repaired).Count(p => p.First != p.Second), 1, 9);
    }

    [Fact]
    public void RefusesUnknownPartition()
    {
        if (!Have) return;
        var (b, t) = Primary();
        Assert.Throws<InvalidDataException>(() => GptRepair.ClearUnbootable(b, t, "no_such_part"));
    }

    [Fact]
    public void CorruptTableIsRejected()
    {
        if (!Have) return;
        var (b, _) = Primary();
        b[2 * S + 100] ^= 0xFF; // damage an entry: entries CRC no longer matches
        Assert.Throws<InvalidDataException>(() => GptRepair.Locate(b, 1, 0));
    }
}
