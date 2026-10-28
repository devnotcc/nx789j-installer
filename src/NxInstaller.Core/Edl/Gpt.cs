using System.Buffers.Binary;
using System.Text;

namespace NxInstaller.Core.Edl;

public sealed record GptPartition(int Lun, string Label, ulong FirstSector, ulong LastSector, ulong Attributes)
{
    public ulong Sectors => LastSector - FirstSector + 1;
    public ulong SizeBytes(int sectorSize) => Sectors * (ulong)sectorSize;
}

/// <summary>
/// Parses a GUID partition table from the start of a LUN (protective MBR + header + entries).
/// The phone's UFS uses 4096-byte sectors, so LBA 1 is at byte 4096. Both CRCs are checked: a
/// table that fails them is not used to compute write offsets.
/// </summary>
public static class Gpt
{
    public const int SectorSize = 4096;
    /// <summary>Sectors read per LUN: MBR + header + 4 sectors of entries (128 x 128 bytes).</summary>
    public const int HeadSectors = 6;

    public static List<GptPartition> Parse(ReadOnlySpan<byte> head, int lun, int sectorSize = SectorSize)
    {
        if (head.Length < 2 * sectorSize) throw new InvalidDataException($"LUN {lun}: GPT read too short");
        var h = head.Slice(sectorSize, sectorSize);
        if (Encoding.ASCII.GetString(h[..8]) != "EFI PART")
            throw new InvalidDataException($"LUN {lun}: no GPT header (signature missing)");

        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(h[12..]);
        var headerCrc = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
        var hdr = h[..headerSize].ToArray();
        hdr.AsSpan(16, 4).Clear();
        if (Crc32.Compute(hdr) != headerCrc) throw new InvalidDataException($"LUN {lun}: GPT header CRC mismatch");

        var entriesLba = BinaryPrimitives.ReadUInt64LittleEndian(h[72..]);
        var count = BinaryPrimitives.ReadInt32LittleEndian(h[80..]);
        var entrySize = BinaryPrimitives.ReadInt32LittleEndian(h[84..]);
        var entriesCrc = BinaryPrimitives.ReadUInt32LittleEndian(h[88..]);

        var start = checked((int)entriesLba * sectorSize);
        var len = count * entrySize;
        if (start + len > head.Length) throw new InvalidDataException($"LUN {lun}: GPT entries beyond the data read");
        var entries = head.Slice(start, len);
        if (Crc32.Compute(entries) != entriesCrc) throw new InvalidDataException($"LUN {lun}: GPT entries CRC mismatch");

        var list = new List<GptPartition>();
        for (var i = 0; i < count; i++)
        {
            var e = entries.Slice(i * entrySize, entrySize);
            if (e[..16].IndexOfAnyExcept((byte)0) < 0) continue; // unused entry (zero type GUID)
            var first = BinaryPrimitives.ReadUInt64LittleEndian(e[32..]);
            var last = BinaryPrimitives.ReadUInt64LittleEndian(e[40..]);
            var attrs = BinaryPrimitives.ReadUInt64LittleEndian(e[48..]);
            var name = Encoding.Unicode.GetString(e.Slice(56, 72)).TrimEnd('\0');
            list.Add(new GptPartition(lun, name, first, last, attrs));
        }
        return list;
    }
}

/// <summary>The partition tables of every LUN, looked up by label.</summary>
public sealed class PartitionTable(IReadOnlyList<GptPartition> partitions)
{
    public IReadOnlyList<GptPartition> All { get; } = partitions;

    public GptPartition? Find(string label)
    {
        var matches = All.Where(p => p.Label.Equals(label, StringComparison.Ordinal)).ToList();
        if (matches.Count > 1) throw new InvalidDataException($"partition label {label} appears {matches.Count} times");
        return matches.FirstOrDefault();
    }

    public GptPartition Get(string label) =>
        Find(label) ?? throw new InvalidDataException($"partition {label} not found on this phone");
}

static class Crc32
{
    static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
