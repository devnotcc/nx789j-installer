using NxInstaller.Core.Edl;
using NxInstaller.Core.Packages;
using NxInstaller.Core.Safety;

namespace NxInstaller.Core.Install;

/// <summary>A prepared image (local raw file) for one manifest entry.</summary>
public sealed record PreparedImage(PackageImage Image, string LocalFile);

/// <summary>
/// The start of super rewritten for the active slot (see <see cref="SuperMetadata"/>): written right
/// after the super image, over its first MiB.
/// </summary>
public sealed record SuperHead(string File, byte[] Bytes, string FromSlot, string ToSlot);

public sealed record InstallPlan(string ActiveSlot, IReadOnlyList<EdlWrite> Writes, IReadOnlyList<EdlWrite> Wipes, SuperHead? Head = null)
{
    public IEnumerable<string> Describe()
    {
        foreach (var w in Writes)
            yield return Head is not null && w.File == Head.File
                ? $"write {Path.GetFileName(w.File)} -> {w.Partition.Label} (first {w.Sectors * Gpt.SectorSize / (1 << 20)} MiB: logical partitions moved from slot {Head.FromSlot} to {Head.ToSlot})"
                : $"write {Path.GetFileName(w.File)} -> {w.Partition.Label} (LUN {w.Partition.Lun}, {w.Sectors * Gpt.SectorSize / (1 << 20)} MiB)";
        foreach (var w in Wipes) yield return $"wipe  {w.Partition.Label} (zero first {w.Sectors * Gpt.SectorSize / (1 << 20)} MiB)";
    }
}

/// <summary>Turns a manifest plus the phone's own partition table into exact writes, refusing anything off.</summary>
public static class InstallPlanner
{
    /// <summary>Zeros written over the start of metadata and userdata to format them, as the bootloader-era scripts did.</summary>
    public const long WipeBytes = 64L << 20;

    /// <summary>
    /// The A/B slot the bootloader will boot, from the GPT attributes of boot_a/boot_b
    /// (Qualcomm: bit 50 = active). Exactly one must be active.
    /// </summary>
    public static string ActiveSlot(PartitionTable table)
    {
        const ulong activeBit = 1UL << 50;
        var a = (table.Get("boot_a").Attributes & activeBit) != 0;
        var b = (table.Get("boot_b").Attributes & activeBit) != 0;
        return (a, b) switch
        {
            (true, false) => "_a",
            (false, true) => "_b",
            _ => throw new InvalidDataException("cannot tell which slot is active (boot_a/boot_b flags are inconsistent); not writing anything"),
        };
    }

    /// <param name="zeroFile">Returns a file of zeros of the given length (in the images' folder).</param>
    public static InstallPlan Plan(PartitionTable table, IReadOnlyList<PreparedImage> images, bool wipe, Func<long, string> zeroFile)
    {
        var slot = ActiveSlot(table);
        var writes = new List<EdlWrite>();
        foreach (var (img, file) in images)
        {
            var label = img.Slot == SlotTarget.Active ? img.Partition + slot : img.Partition;
            PartitionPolicy.Require(label, WriteKind.Image);
            var part = table.Get(label);
            var len = new FileInfo(file).Length;
            if (len != img.Size) throw new InvalidDataException($"{img.File}: {len} bytes on disk, manifest says {img.Size}");
            if (len % Gpt.SectorSize != 0) throw new InvalidDataException($"{img.File}: size is not a multiple of {Gpt.SectorSize}");
            var sectors = (ulong)len / Gpt.SectorSize;
            if (sectors > part.Sectors)
                throw new InvalidDataException($"{img.File} ({len >> 20} MiB) is larger than {label} ({part.SizeBytes(Gpt.SectorSize) >> 20} MiB) on this phone");
            if (writes.Any(w => w.Partition == part)) throw new InvalidDataException($"{label} is written twice");
            writes.Add(new EdlWrite(part, file, WriteKind.Image, sectors));
        }

        // the super image holds data for the slot it was built for; on the other slot the new boot
        // images would find empty logical partitions, so its metadata is moved to the active slot
        SuperHead? head = null;
        var super = writes.FirstOrDefault(w => PartitionPolicy.BaseName(w.Partition.Label) == "super");
        if (super is not null)
        {
            var bytes = SuperMetadata.ReadHead(super.File);
            var built = SuperMetadata.PopulatedSlot(bytes);
            if (built is not null && built != slot)
            {
                if (bytes.Length != SuperMetadata.HeadBytes) throw new InvalidDataException("super image is shorter than its metadata area");
                var swapped = SuperMetadata.SwapSlots(bytes);
                if (SuperMetadata.PopulatedSlot(swapped) != slot) throw new InvalidDataException($"could not move the super metadata to slot {slot}");
                var path = Path.Combine(Path.GetDirectoryName(super.File)!, $"super_head{slot}.img");
                File.WriteAllBytes(path, swapped);
                head = new SuperHead(path, swapped, built, slot);
                writes.Insert(writes.IndexOf(super) + 1, new EdlWrite(super.Partition, path, WriteKind.Image, (ulong)swapped.Length / Gpt.SectorSize));
            }
        }

        var wipes = new List<EdlWrite>();
        if (wipe)
        {
            foreach (var label in new[] { "metadata", "userdata" })
            {
                PartitionPolicy.Require(label, WriteKind.Wipe);
                var part = table.Get(label);
                var sectors = Math.Min(part.Sectors, (ulong)(WipeBytes / Gpt.SectorSize));
                wipes.Add(new EdlWrite(part, zeroFile((long)sectors * Gpt.SectorSize), WriteKind.Wipe, sectors));
            }
        }
        return new InstallPlan(slot, writes, wipes, head);
    }

    /// <summary>Whether this install formats data, from the manifest's rule and the OS the phone runs now.</summary>
    public static bool NeedsWipe(PackageManifest m, string? currentFamily) =>
        m.Wipe.Always || currentFamily is null || !m.Wipe.UnlessFamily.Contains(currentFamily, StringComparer.OrdinalIgnoreCase);

    public static string ZeroFile(string dir, long bytes)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"zeros_{bytes}.img");
        if (File.Exists(path) && new FileInfo(path).Length == bytes) return path;
        using var f = new FileStream(path, FileMode.Create, FileAccess.Write);
        f.SetLength(bytes); // NTFS returns zeros for the unwritten range
        return path;
    }
}
