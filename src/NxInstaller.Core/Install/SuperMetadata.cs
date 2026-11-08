using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace NxInstaller.Core.Install;

/// <summary>
/// The dynamic-partition (liblp) metadata at the start of a super image.
///
/// A super image built by the ROM build has data only in the slot it was built for (system_a,
/// vendor_a, ... with extents; system_b, ... empty). A phone running on slot _b would boot the
/// new boot images and find an empty system ("no OS installed"). <see cref="SwapSlots"/> renames
/// every _a logical partition and group to _b and back, in every metadata copy, so the same data
/// belongs to the other slot: the state a phone is in after an OTA to that slot. Only the first
/// <see cref="HeadBytes"/> of super change; the data itself (from 1 MiB on) stays as built.
/// </summary>
public static class SuperMetadata
{
    /// <summary>How much of the start of super is rewritten (the data starts at 1 MiB).</summary>
    public const int HeadBytes = 1 << 20;

    const uint GeometryMagic = 0x616c4467, HeaderMagic = 0x414c5030;
    const int Reserved = 4096, GeometrySize = 4096;

    sealed record Copy(int Offset, int HeaderSize, int TablesSize, (int Off, int N, int Size) Parts, (int Off, int N, int Size) Groups);

    /// <summary>Reads the start of an image file (<see cref="HeadBytes"/> or less if the file is shorter).</summary>
    public static byte[] ReadHead(string file)
    {
        using var f = File.OpenRead(file);
        var head = new byte[Math.Min(HeadBytes, f.Length)];
        f.ReadExactly(head);
        return head;
    }

    static IReadOnlyList<Copy> Copies(byte[] head)
    {
        if (head.Length < Reserved + 2 * GeometrySize) throw new InvalidDataException("super image too short for liblp metadata");
        var g = head.AsSpan(Reserved, GeometrySize);
        if (BinaryPrimitives.ReadUInt32LittleEndian(g) != GeometryMagic) throw new InvalidDataException("super image has no liblp geometry");
        var gsize = BinaryPrimitives.ReadInt32LittleEndian(g[4..]);
        if (gsize is < 52 or > GeometrySize) throw new InvalidDataException("liblp geometry has a bad size");
        var gcopy = g[..gsize].ToArray();
        Array.Clear(gcopy, 8, 32);
        if (!SHA256.HashData(gcopy).AsSpan().SequenceEqual(g.Slice(8, 32))) throw new InvalidDataException("liblp geometry checksum is wrong");
        var maxSize = BinaryPrimitives.ReadInt32LittleEndian(g[40..]);
        var slots = BinaryPrimitives.ReadInt32LittleEndian(g[44..]);
        if (maxSize <= 0 || slots is <= 0 or > 4) throw new InvalidDataException("liblp geometry has bad metadata sizes");

        var copies = new List<Copy>();
        var first = Reserved + 2 * GeometrySize;
        for (var i = 0; i < 2 * slots; i++)       // primary slots, then backup slots
        {
            var off = first + i * maxSize;
            if (off + maxSize > head.Length) throw new InvalidDataException("liblp metadata does not fit in the first MiB of super");
            var h = head.AsSpan(off);
            if (BinaryPrimitives.ReadUInt32LittleEndian(h) != HeaderMagic) throw new InvalidDataException($"liblp metadata copy {i} is missing");
            var hsize = BinaryPrimitives.ReadInt32LittleEndian(h[8..]);
            var tsize = BinaryPrimitives.ReadInt32LittleEndian(h[44..]);
            if (hsize is < 128 or > 1024 || tsize < 0 || hsize + tsize > maxSize) throw new InvalidDataException($"liblp metadata copy {i} has bad sizes");
            if (!SHA256.HashData(h.Slice(hsize, tsize)).AsSpan().SequenceEqual(h.Slice(48, 32))) throw new InvalidDataException($"liblp metadata copy {i}: tables checksum is wrong");
            var hcopy = h[..hsize].ToArray();
            Array.Clear(hcopy, 12, 32);
            if (!SHA256.HashData(hcopy).AsSpan().SequenceEqual(h.Slice(12, 32))) throw new InvalidDataException($"liblp metadata copy {i}: header checksum is wrong");
            var parts = Desc(head, off + 80);
            var groups = Desc(head, off + 104);
            if (parts.Item3 < 52 || groups.Item3 < 48 || parts.Item1 + parts.Item2 * parts.Item3 > tsize || groups.Item1 + groups.Item2 * groups.Item3 > tsize)
                throw new InvalidDataException($"liblp metadata copy {i} has bad tables");
            copies.Add(new Copy(off, hsize, tsize, parts, groups));
        }
        return copies;
    }

    /// <summary>A table descriptor: offset in the tables, entry count, entry size.</summary>
    static (int Off, int N, int Size) Desc(byte[] b, int at) =>
        (BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at)), BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at + 4)), BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at + 8)));

    static string Name(ReadOnlySpan<byte> entry)
    {
        var n = entry[..36];
        var end = n.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? n : n[..end]);
    }

    /// <summary>
    /// The slot ("_a" or "_b") whose logical partitions hold data in this super image, or null when
    /// neither or both do (nothing to adjust).
    /// </summary>
    public static string? PopulatedSlot(byte[] head)
    {
        var c = Copies(head)[0];
        var tables = head.AsSpan(c.Offset + c.HeaderSize, c.TablesSize);
        var found = new HashSet<string>();
        for (var i = 0; i < c.Parts.N; i++)
        {
            var e = tables.Slice(c.Parts.Off + i * c.Parts.Size, c.Parts.Size);
            var name = Name(e);
            if (BinaryPrimitives.ReadUInt32LittleEndian(e[44..]) > 0 && (name.EndsWith("_a") || name.EndsWith("_b")))
                found.Add(name[^2..]);
        }
        return found.Count == 1 ? found.First() : null;
    }

    /// <summary>A copy of <paramref name="head"/> with _a and _b swapped in every partition and group name of every metadata copy.</summary>
    public static byte[] SwapSlots(byte[] head)
    {
        var copies = Copies(head);
        var o = (byte[])head.Clone();
        foreach (var c in copies)
        {
            var tables = o.AsSpan(c.Offset + c.HeaderSize, c.TablesSize);
            foreach (var (off, n, size) in new[] { c.Parts, c.Groups })
                for (var i = 0; i < n; i++)
                {
                    var e = tables.Slice(off + i * size, 36);
                    var len = Name(e).Length;
                    if (len >= 2 && e[len - 2] == '_' && e[len - 1] is (byte)'a' or (byte)'b')
                        e[len - 1] = e[len - 1] == 'a' ? (byte)'b' : (byte)'a';
                }
            var h = o.AsSpan(c.Offset);
            SHA256.HashData(tables).CopyTo(h.Slice(48, 32));
            h.Slice(12, 32).Clear();
            SHA256.HashData(h[..c.HeaderSize]).CopyTo(h.Slice(12, 32));
        }
        Copies(o); // checksums of the result
        return o;
    }

    /// <summary>SHA-256 of <paramref name="file"/> with its first bytes replaced by <paramref name="head"/>: what super reads back as.</summary>
    public static string Sha256WithHead(string file, byte[] head, ulong totalBytes)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(head);
        using var f = File.OpenRead(file);
        f.Position = head.Length;
        var buf = new byte[4 << 20];
        var left = (long)totalBytes - head.Length;
        while (left > 0)
        {
            var r = f.Read(buf, 0, (int)Math.Min(buf.Length, left));
            if (r <= 0) throw new EndOfStreamException(file);
            sha.AppendData(buf, 0, r);
            left -= r;
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}
