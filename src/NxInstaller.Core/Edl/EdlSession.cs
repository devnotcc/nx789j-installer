using System.Globalization;
using System.Management;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
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
public sealed partial class EdlSession(EdlTools tools, ProcessRunner run, Log log, string workDir)
{
    public const int Luns = 6;
    public string? Port { get; private set; }
    public PartitionTable? Table { get; private set; }
    public DevInfo? Unlock { get; private set; }

    /// <summary>When set, writes are logged and their XML kept, but nothing is sent to the phone.</summary>
    public bool DryRun { get; init; }

    public static string? FindPort()
    {
        using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
        foreach (var o in s.Get())
        {
            var name = o["Name"] as string ?? "";
            if (!name.Contains("QDLoader") && !name.Contains("9008")) continue;
            var m = ComRx().Match(name);
            if (m.Success) return m.Groups[1].Value;
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

    public async Task ResetAsync(CancellationToken ct)
    {
        if (DryRun) { log.Warn("DRY RUN: would reboot the phone"); return; }
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

    [GeneratedRegex(@"\(COM(\d+)\)")]
    private static partial Regex ComRx();
}

public sealed class EdlException(string message, ProcessResult? result)
    : Exception(result is null ? message : $"{message}\n{Tail(result.Output)}")
{
    static string Tail(string s) => s.Length > 1500 ? "..." + s[^1500..] : s;
}
