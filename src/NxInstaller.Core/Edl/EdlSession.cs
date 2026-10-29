using System.Globalization;
using System.Security;
using System.Text;
using Microsoft.Win32;
using NxInstaller.Core.Safety;

namespace NxInstaller.Core.Edl;

/// <summary>A region to write: an image file over (part of) a partition, or zeros for a wipe.</summary>
public sealed record EdlWrite(GptPartition Partition, string File, WriteKind Kind, ulong Sectors);

/// <summary>A region to read into a file.</summary>
public sealed record EdlRead(GptPartition Partition, string File, ulong Sectors, ulong Offset = 0);

/// <summary>
/// One connection to the phone in EDL (Qualcomm 9008) mode: Sahara loads the firehose, the port is
/// configured for ZTE, then fh_loader runs read / program / reset XMLs. The same sequence the
/// hand-written scripts used, built from the phone's own partition table instead of fixed offsets.
///
/// After <see cref="ConnectAsync"/>, reads are always allowed; writes go through
/// <see cref="PartitionPolicy"/> and require <see cref="Unlock"/> to have been read as unlocked.
/// </summary>
public sealed class EdlSession(EdlTools tools, ProcessRunner run, Log log, string workDir)
{
    public const int Luns = 6;
    public string? Port { get; private set; }
    public PartitionTable? Table { get; private set; }
    public DevInfo? Unlock { get; private set; }

    /// <summary>When set, writes are logged and their XML kept, but nothing is sent to the phone.</summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// The COM port number of a connected Qualcomm 9008 (EDL) device, or null. Windows keeps every
    /// port a USB device ever had under its hardware ID, and lists only the ports present right now
    /// in HARDWARE\DEVICEMAP\SERIALCOMM; a 9008 port is connected when it is in both. Plain registry
    /// reads, no WMI, so it also works in the trimmed release build.
    /// </summary>
    public static string? FindPort()
    {
        using var active = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
        if (active is null) return null;
        var present = active.GetValueNames().Select(n => active.GetValue(n) as string).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usb is null) return null;
        foreach (var id in usb.GetSubKeyNames().Where(n => n.StartsWith("VID_05C6&PID_9008", StringComparison.OrdinalIgnoreCase)))
        {
            using var dev = usb.OpenSubKey(id);
            foreach (var inst in dev?.GetSubKeyNames() ?? [])
            {
                using var p = dev!.OpenSubKey(inst + @"\Device Parameters");
                if (p?.GetValue("PortName") is string port && present.Contains(port) && port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                    return port[3..];
            }
        }
        return null;
    }

    public async Task<string> WaitForPortAsync(TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (FindPort() is { } p) return p;
            await Task.Delay(1500, ct);
        }
        throw new TimeoutException("No Qualcomm 9008 (EDL) device appeared. Check the cable and the Qualcomm driver.");
    }

    /// <summary>Loads the firehose and reads the partition tables and the lock state. Writes nothing.</summary>
    public async Task ConnectAsync(string port, CancellationToken ct)
    {
        Port = port;
        Directory.CreateDirectory(workDir);
        log.Info($"EDL on COM{port}: loading the firehose");
        var r = await run.RunAsync(tools.Sahara, ["-p", $@"\\.\COM{port}", "-s", $"13:{tools.Firehose}"], TimeSpan.FromSeconds(60), ct: ct);
        if (!r.Ok) throw new EdlException("Sahara (firehose upload) failed. Unplug, re-enter EDL and try again.", r);

        var cfg = Path.Combine(workDir, "configure.xml");
        await File.WriteAllTextAsync(cfg, """<?xml version="1.0" encoding="UTF-8" ?><data><configure MemoryName="ufs" Verbose="0" AlwaysValidate="0" MaxDigestTableSizeInBytes="8192" MaxPayloadSizeToTargetInBytes="1048576" ZlpAwareHost="1" SkipStorageInit="0" Oem="ZTE"/></data>""", ct);
        r = await run.RunAsync(tools.CmdHelper, ["-p", $"COM{port}", "-m", "xml", "-f", cfg], TimeSpan.FromSeconds(30), ct: ct);
        if (!r.Ok) throw new EdlException("Configuring the EDL connection failed.", r);

        await ReadTablesAsync(ct);
        await ReadUnlockAsync(ct);
    }

    async Task ReadTablesAsync(CancellationToken ct)
    {
        var parts = new List<GptPartition>();
        for (var lun = 0; lun < Luns; lun++)
        {
            var file = $"gpt_lun{lun}.bin";
            await SendAsync($"""<read filename="{file}" physical_partition_number="{lun}" start_sector="0" num_partition_sectors="{Gpt.HeadSectors}" SECTOR_SIZE_IN_BYTES="{Gpt.SectorSize}" label="PrimaryGPT"/>""", $"read GPT of LUN {lun}", ct);
            parts.AddRange(Gpt.Parse(await File.ReadAllBytesAsync(Path.Combine(workDir, file), ct), lun));
        }
        Table = new PartitionTable(parts);
        log.Info($"partition tables read: {parts.Count} partitions on {Luns} LUNs");
    }

    async Task ReadUnlockAsync(CancellationToken ct)
    {
        var p = Table!.Get("devinfo");
        var file = await ReadAsync(new EdlRead(p, "devinfo.bin", 1), ct);
        Unlock = DevInfo.Parse(await File.ReadAllBytesAsync(file, ct));
        log.Info($"bootloader: {(Unlock.Unlocked ? "unlocked" : "LOCKED")} (critical {(Unlock.CriticalUnlocked ? "unlocked" : "locked")})");
    }

    /// <summary>Reads sectors of a partition into workDir (or an absolute path). Returns the file path.</summary>
    public async Task<string> ReadAsync(EdlRead read, CancellationToken ct, string? outDir = null)
    {
        var p = read.Partition;
        if (read.Offset + read.Sectors > p.Sectors) throw new ArgumentOutOfRangeException(nameof(read), "read past the end of the partition");
        var dir = outDir ?? workDir;
        Directory.CreateDirectory(dir);
        var file = Path.GetFileName(read.File);
        File.Delete(Path.Combine(dir, file));
        await SendAsync($"""<read filename="{Esc(file)}" label="{Esc(p.Label)}" physical_partition_number="{p.Lun}" start_sector="{p.FirstSector + read.Offset}" num_partition_sectors="{read.Sectors}" SECTOR_SIZE_IN_BYTES="{Gpt.SectorSize}"/>""",
            $"read {p.Label} ({Size(read.Sectors)})", ct, dir);
        var path = Path.Combine(dir, file);
        if (!File.Exists(path) || new FileInfo(path).Length != (long)read.Sectors * Gpt.SectorSize)
            throw new EdlException($"Reading {p.Label} returned no or short data.", null);
        return path;
    }

    /// <summary>Writes images or wipes. Every target is re-checked against the policy and the lock state here.</summary>
    public async Task WriteAsync(IReadOnlyList<EdlWrite> writes, CancellationToken ct)
    {
        if (Table is null || Unlock is null) throw new InvalidOperationException("not connected");
        if (!Unlock.Unlocked) throw new PolicyViolationException("The bootloader is locked. Writing would leave the phone unbootable; nothing was written.");

        var xml = new StringBuilder("<?xml version=\"1.0\" ?>\n<data>\n");
        foreach (var w in writes)
        {
            PartitionPolicy.Require(w.Partition.Label, w.Kind);
            var live = Table.Get(w.Partition.Label);
            if (live != w.Partition) throw new PolicyViolationException($"{w.Partition.Label} moved since the plan was made");
            if (w.Sectors == 0 || w.Sectors > live.Sectors) throw new PolicyViolationException($"{w.File} does not fit in {live.Label}");
            if (!File.Exists(w.File)) throw new FileNotFoundException(w.File);
            var len = new FileInfo(w.File).Length;
            if (len != (long)w.Sectors * Gpt.SectorSize) throw new PolicyViolationException($"{Path.GetFileName(w.File)} is {len} bytes, expected {w.Sectors} sectors");
            xml.Append(CultureInfo.InvariantCulture,
                $"""  <program filename="{Esc(Path.GetFileName(w.File))}" label="{Esc(live.Label)}" physical_partition_number="{live.Lun}" start_sector="{live.FirstSector}" num_partition_sectors="{w.Sectors}" SECTOR_SIZE_IN_BYTES="{Gpt.SectorSize}"/>""").Append('\n');
        }
        xml.Append("</data>\n");

        var dirs = writes.Select(w => Path.GetDirectoryName(w.File)!).Distinct().ToList();
        if (dirs.Count != 1) throw new ArgumentException("all images of one write must be in one folder");
        var what = string.Join(", ", writes.Select(w => w.Partition.Label));
        if (DryRun)
        {
            var path = Path.Combine(workDir, "program-dryrun.xml");
            await File.WriteAllTextAsync(path, xml.ToString(), ct);
            log.Warn($"DRY RUN: would write {what}; XML kept at {path}");
            return;
        }
        log.Info($"writing {what}");
        await SendAsync(xml.ToString(), $"write {what}", ct, dirs[0], isFullXml: true, timeout: TimeSpan.FromHours(2));
    }

    /// <summary>
    /// Clears the "unbootable" mark on <paramref name="slot"/> ("_a"/"_b") in BOTH the primary and
    /// the backup GPT of the LUN holding boot_x, then reads both back to check. Returns false when
    /// the slot isn't marked (nothing written). See <see cref="GptRepair"/>.
    /// </summary>
    public async Task<bool> RepairSlotAsync(string slot, CancellationToken ct)
    {
        if (Table is null || Unlock is null) throw new InvalidOperationException("not connected");
        if (!Unlock.Unlocked) throw new PolicyViolationException("The bootloader is locked; nothing was written.");
        var label = "boot" + slot;
        var lun = Table.Get(label).Lun;

        // primary: MBR + header + entries; backup: entries + header at the end of the LUN
        var pFile = await ReadRegionAsync(lun, 0, Gpt.HeadSectors, "gpt_primary.bin", ct);
        var primary = await File.ReadAllBytesAsync(pFile, ct);
        var pt = GptRepair.Locate(primary, 1, 0);
        var bStart = pt.AlternateLba - 4;
        var bFile = await ReadRegionAsync(lun, bStart, 5, "gpt_backup.bin", ct);
        var backup = await File.ReadAllBytesAsync(bFile, ct);
        var bt = GptRepair.Locate(backup, 4, bStart);
        if (bt.AlternateLba != 1) throw new InvalidDataException("backup GPT does not point back at the primary; not touching it");

        var pAttr = GptRepair.Attributes(primary, pt, label);
        var bAttr = GptRepair.Attributes(backup, bt, label);
        log.Info($"{label} attributes: primary {pAttr:x16}, backup {bAttr:x16}");
        const ulong mark = 1UL << GptRepair.UnbootableBit;
        if (((pAttr ?? 0) & mark) == 0 && ((bAttr ?? 0) & mark) == 0) { log.Info($"{label} is not marked unbootable"); return false; }

        // Patch each copy that carries the mark; the other is left as it is.
        var dir = Path.Combine(workDir, "gptfix");
        Directory.CreateDirectory(dir);
        var writes = new StringBuilder("<?xml version=\"1.0\" ?>\n<data>\n");
        var expect = new List<(ulong Start, int Sectors, byte[] Data, string File)>();
        if (((pAttr ?? 0) & mark) != 0)
        {
            var fixedP = GptRepair.ClearUnbootable(primary, pt, label);
            await File.WriteAllBytesAsync(Path.Combine(dir, "gpt_primary_fixed.bin"), fixedP, ct);
            writes.Append($"""  <program filename="gpt_primary_fixed.bin" label="PrimaryGPT" physical_partition_number="{lun}" start_sector="0" num_partition_sectors="{Gpt.HeadSectors}" SECTOR_SIZE_IN_BYTES="{Gpt.SectorSize}"/>""").Append('\n');
            expect.Add((0, Gpt.HeadSectors, fixedP, "gpt_primary_check.bin"));
        }
        if (((bAttr ?? 0) & mark) != 0)
        {
            var fixedB = GptRepair.ClearUnbootable(backup, bt, label);
            await File.WriteAllBytesAsync(Path.Combine(dir, "gpt_backup_fixed.bin"), fixedB, ct);
            writes.Append($"""  <program filename="gpt_backup_fixed.bin" label="BackupGPT" physical_partition_number="{lun}" start_sector="{bStart}" num_partition_sectors="5" SECTOR_SIZE_IN_BYTES="{Gpt.SectorSize}"/>""").Append('\n');
            expect.Add((bStart, 5, fixedB, "gpt_backup_check.bin"));
        }
        writes.Append("</data>\n");

        if (DryRun)
        {
            await File.WriteAllTextAsync(Path.Combine(workDir, "gptfix-dryrun.xml"), writes.ToString(), ct);
            log.Warn($"DRY RUN: would clear the unbootable mark on {label} ({expect.Count} table copies)");
            return true;
        }
        log.Info($"clearing the unbootable mark on {label} in {expect.Count} table copies");
        await SendAsync(writes.ToString(), "repair slot", ct, dir, isFullXml: true);

        foreach (var (start, sectors, data, file) in expect)
        {
            var back = await File.ReadAllBytesAsync(await ReadRegionAsync(lun, start, sectors, file, ct), ct);
            if (!back.AsSpan().SequenceEqual(data)) throw new EdlException($"The partition table at sector {start} reads back different from what was written.", null);
        }
        log.Info($"{label} is bootable again (both table copies checked)");
        return true;
    }

    /// <summary>Reads raw sectors outside any partition (partition tables).</summary>
    async Task<string> ReadRegionAsync(int lun, ulong start, int sectors, string file, CancellationToken ct)
    {
        File.Delete(Path.Combine(workDir, file));
        await SendAsync($"""<read filename="{file}" physical_partition_number="{lun}" start_sector="{start}" num_partition_sectors="{sectors}" SECTOR_SIZE_IN_BYTES="{Gpt.SectorSize}" label="gpt"/>""",
            $"read sectors {start}+{sectors} of LUN {lun}", ct);
        var path = Path.Combine(workDir, file);
        if (!File.Exists(path) || new FileInfo(path).Length != (long)sectors * Gpt.SectorSize)
            throw new EdlException($"Reading LUN {lun} sectors {start}+{sectors} returned no or short data.", null);
        return path;
    }

    public async Task ResetAsync(CancellationToken ct)
    {
        // a reboot writes nothing, and skipping it in a dry run would leave the phone sitting in EDL
        await SendAsync("""<power DelayInSeconds="0" value="reset" />""", "reboot", ct);
    }

    async Task SendAsync(string body, string what, CancellationToken ct, string? searchDir = null, bool isFullXml = false, TimeSpan? timeout = null)
    {
        var xml = isFullXml ? body : $"<?xml version=\"1.0\" ?>\n<data>\n  {body}\n</data>\n";
        var xmlPath = Path.Combine(workDir, $"cmd-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(xmlPath, xml, ct);
        var outDir = searchDir ?? workDir;
        var r = await run.RunAsync(tools.FhLoader,
            [$@"--port=\\.\COM{Port}", "--memoryname=ufs", $"--search_path={searchDir ?? workDir}", $"--sendxml={xmlPath}",
             $"--mainoutputdir={outDir}", "--skip_config", "--noprompt"],
            timeout ?? TimeSpan.FromMinutes(30), ct: ct, quiet: true);
        File.Delete(xmlPath);
        if (!r.Ok) throw new EdlException($"EDL command failed: {what}.", r);
        log.Debug($"EDL ok: {what}");
    }

    static string Esc(string s) => SecurityElement.Escape(s);
    static string Size(ulong sectors) => sectors * Gpt.SectorSize >= 1 << 20
        ? $"{sectors * Gpt.SectorSize / (1 << 20)} MiB" : $"{sectors * Gpt.SectorSize / 1024} KiB";
}

public sealed class EdlException(string message, ProcessResult? result)
    : Exception(result is null ? message : $"{message}\n{Tail(result.Output)}")
{
    static string Tail(string s) => s.Length > 1500 ? "..." + s[^1500..] : s;
}
