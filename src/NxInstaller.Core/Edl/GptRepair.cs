using System.Buffers.Binary;
using System.Text;

namespace NxInstaller.Core.Edl;

/// <summary>
/// Clears the bootloader's "unbootable" mark on a slot: bit 55 of the boot_x entry's attributes in
/// the GPT of the LUN that holds the A/B boot chain (LUN 4 on the NX789J).
///
/// Failed boots set that bit; the phone then only starts into fastboot, and nubia's fastboot can't
/// change it. Both copies of the table must be fixed: the bootloader repairs a primary GPT that
/// disagrees with the backup FROM the backup, which puts the mark straight back (seen 2026-09-16).
///
/// Everything here works on byte buffers so it can be checked without a phone. The patch refuses
/// to produce a table that differs from the original in anything but that one bit and the CRCs.
/// </summary>
public static class GptRepair
{
    public const int UnbootableBit = 55;
    const int S = Gpt.SectorSize;

    /// <summary>A GPT header plus its entry array, located inside a buffer read from the LUN.</summary>
    public sealed record Table(int HeaderOffset, int EntriesOffset, int Count, int EntrySize, ulong AlternateLba, ulong EntriesLba);

    /// <summary>Finds the header (at <paramref name="headerSector"/> of the buffer) and checks both CRCs.</summary>
    public static Table Locate(ReadOnlySpan<byte> buf, int headerSector, ulong bufferStartLba)
    {
        var h = headerSector * S;
        if (buf.Length < h + S || Encoding.ASCII.GetString(buf.Slice(h, 8)) != "EFI PART")
            throw new InvalidDataException("no GPT header where expected");
        var hsize = BinaryPrimitives.ReadInt32LittleEndian(buf[(h + 12)..]);
        var hdr = buf.Slice(h, hsize).ToArray();
        var hcrc = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(16));
        hdr.AsSpan(16, 4).Clear();
        if (Crc32.Compute(hdr) != hcrc) throw new InvalidDataException("GPT header CRC mismatch");

        var alt = BinaryPrimitives.ReadUInt64LittleEndian(buf[(h + 32)..]);
        var elba = BinaryPrimitives.ReadUInt64LittleEndian(buf[(h + 72)..]);
        var count = BinaryPrimitives.ReadInt32LittleEndian(buf[(h + 80)..]);
        var esize = BinaryPrimitives.ReadInt32LittleEndian(buf[(h + 84)..]);
        var ecrc = BinaryPrimitives.ReadUInt32LittleEndian(buf[(h + 88)..]);
        if (elba < bufferStartLba) throw new InvalidDataException("GPT entries lie before the data read");
        var eoff = checked((int)(elba - bufferStartLba) * S);
        if (eoff + count * esize > buf.Length) throw new InvalidDataException("GPT entries beyond the data read");
        if (Crc32.Compute(buf.Slice(eoff, count * esize)) != ecrc) throw new InvalidDataException("GPT entries CRC mismatch");
        return new Table(h, eoff, count, esize, alt, elba);
    }

    /// <summary>Attributes of the named entry, or null if the table has no such entry.</summary>
    public static ulong? Attributes(ReadOnlySpan<byte> buf, Table t, string label)
    {
        var i = IndexOf(buf, t, label);
        return i < 0 ? null : BinaryPrimitives.ReadUInt64LittleEndian(buf[(t.EntriesOffset + i * t.EntrySize + 48)..]);
    }

    /// <summary>
    /// Returns a copy of <paramref name="buf"/> with the unbootable bit of <paramref name="label"/>
    /// cleared and both CRCs recomputed. Throws if the bit isn't set (nothing to repair) or if the
    /// result differs from the input anywhere else.
    /// </summary>
    public static byte[] ClearUnbootable(ReadOnlySpan<byte> buf, Table t, string label)
    {
        var i = IndexOf(buf, t, label);
        if (i < 0) throw new InvalidDataException($"{label} is not in this partition table");
        var d = buf.ToArray();
        var at = t.EntriesOffset + i * t.EntrySize + 48;
        var attr = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(at));
        if ((attr & (1UL << UnbootableBit)) == 0) throw new InvalidOperationException($"{label} is not marked unbootable");
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(at), attr & ~(1UL << UnbootableBit));

        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(t.HeaderOffset + 88), Crc32.Compute(d.AsSpan(t.EntriesOffset, t.Count * t.EntrySize)));
        var hsize = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(t.HeaderOffset + 12));
        d.AsSpan(t.HeaderOffset + 16, 4).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(t.HeaderOffset + 16), Crc32.Compute(d.AsSpan(t.HeaderOffset, hsize)));

        // Only the attribute byte holding bit 55 and the two CRCs may differ.
        var allowed = new HashSet<int> { at + UnbootableBit / 8 };
        for (var k = 0; k < 4; k++) { allowed.Add(t.HeaderOffset + 88 + k); allowed.Add(t.HeaderOffset + 16 + k); }
        for (var k = 0; k < d.Length; k++)
            if (d[k] != buf[k] && !allowed.Contains(k)) throw new InvalidDataException($"repair would change byte {k}; refusing");
        Locate(d, t.HeaderOffset / S, t.EntriesLba - (ulong)(t.EntriesOffset / S)); // still a valid table
        return d;
    }

    static int IndexOf(ReadOnlySpan<byte> buf, Table t, string label)
    {
        var found = -1;
        for (var i = 0; i < t.Count; i++)
        {
            var e = buf.Slice(t.EntriesOffset + i * t.EntrySize, t.EntrySize);
            if (e[..16].IndexOfAnyExcept((byte)0) < 0) continue;
            if (Encoding.Unicode.GetString(e.Slice(56, 72)).TrimEnd('\0') != label) continue;
            if (found >= 0) throw new InvalidDataException($"{label} appears twice in the table");
            found = i;
        }
        return found;
    }
}
