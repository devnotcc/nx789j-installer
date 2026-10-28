using System.Buffers.Binary;
using System.Text;
using NxInstaller.Core.Edl;
using Xunit;

namespace NxInstaller.Core.Tests;

public class GptTests
{
    const int Sector = 4096;

    /// <summary>Builds a minimal but CRC-correct GPT (MBR + header + entries) for the parser.</summary>
    static byte[] BuildGpt((string name, ulong first, ulong last, ulong attrs)[] parts)
    {
        var entriesSectors = 4;
        var buf = new byte[Sector * (2 + entriesSectors)];
        var entries = buf.AsSpan(2 * Sector, entriesSectors * Sector);
        for (var i = 0; i < parts.Length; i++)
        {
            var e = entries.Slice(i * 128, 128);
            Guid.NewGuid().TryWriteBytes(e[..16]);  // type GUID (non-zero = used)
            Guid.NewGuid().TryWriteBytes(e[16..32]);
            BinaryPrimitives.WriteUInt64LittleEndian(e[32..], parts[i].first);
            BinaryPrimitives.WriteUInt64LittleEndian(e[40..], parts[i].last);
            BinaryPrimitives.WriteUInt64LittleEndian(e[48..], parts[i].attrs);
            Encoding.Unicode.GetBytes(parts[i].name).CopyTo(e[56..]);
        }

        var h = buf.AsSpan(Sector, Sector);
        Encoding.ASCII.GetBytes("EFI PART").CopyTo(h);
        BinaryPrimitives.WriteInt32LittleEndian(h[12..], 92);       // header size
        BinaryPrimitives.WriteUInt64LittleEndian(h[72..], 2);       // entries LBA
        BinaryPrimitives.WriteInt32LittleEndian(h[80..], 128);      // entry count
        BinaryPrimitives.WriteInt32LittleEndian(h[84..], 128);      // entry size
        BinaryPrimitives.WriteUInt32LittleEndian(h[88..], Crc32(entries));
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 0);       // crc field zeroed for calc
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], Crc32(h[..92]));
        return buf;
    }

    static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            c ^= b;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }
        return c ^ 0xFFFFFFFFu;
    }

    [Fact]
    public void ParsesPartitions()
    {
        var gpt = BuildGpt([("super", 100, 199, 0), ("boot_a", 200, 209, 1UL << 50)]);
        var parts = Gpt.Parse(gpt, lun: 0);
        Assert.Equal(2, parts.Count);
        var super = parts[0];
        Assert.Equal("super", super.Label);
        Assert.Equal(100u, super.FirstSector);
        Assert.Equal(100u, super.Sectors);
        Assert.Equal((ulong)100 * Sector, super.SizeBytes(Sector));
    }

    [Fact]
    public void SkipsUnusedEntries()
    {
        var gpt = BuildGpt([("one", 10, 19, 0)]);
        Assert.Single(Gpt.Parse(gpt, 0));
    }

    [Fact]
    public void RejectsBadEntriesCrc()
    {
        var gpt = BuildGpt([("x", 10, 19, 0)]);
        gpt[2 * Sector + 40] ^= 0xFF; // flip a byte inside the entries
        Assert.Throws<InvalidDataException>(() => Gpt.Parse(gpt, 0));
    }

    [Fact]
    public void RejectsMissingSignature()
    {
        var gpt = BuildGpt([("x", 10, 19, 0)]);
        gpt[Sector] = 0;
        Assert.Throws<InvalidDataException>(() => Gpt.Parse(gpt, 0));
    }

    [Fact]
    public void TableFindRejectsDuplicates()
    {
        var parts = Gpt.Parse(BuildGpt([("dup", 10, 19, 0), ("dup", 20, 29, 0)]), 0);
        var table = new PartitionTable(parts);
        Assert.Throws<InvalidDataException>(() => table.Find("dup"));
    }

    [Fact]
    public void TableGetThrowsWhenMissing()
    {
        var table = new PartitionTable(Gpt.Parse(BuildGpt([("a", 1, 2, 0)]), 0));
        Assert.Throws<InvalidDataException>(() => table.Get("nope"));
    }
}
