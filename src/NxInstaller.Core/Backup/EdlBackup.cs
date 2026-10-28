using System.Text.Json;
using NxInstaller.Core.Edl;

namespace NxInstaller.Core.Backup;

public sealed record BackupEntry(int Lun, string Label, ulong StartSector, ulong Sectors, string File, string Sha256);

public sealed record BackupManifest(DateTime Created, string? Slot, bool Unlocked, List<BackupEntry> Partitions, List<BackupEntry> Tables);

/// <summary>
/// Reads every partition of every LUN to the PC over EDL, plus each LUN's primary and backup GPT.
/// Read-only: it never writes to the phone. userdata is skipped by default (it is the size of the
/// phone's storage and encrypted with keys that live in the phone's TEE, so a copy is of no use
/// on its own), and so is super unless asked, since it is 16 GiB and fully rebuildable from an OS package.
/// </summary>
public sealed class EdlBackup(EdlSession edl, Log log)
{
    public bool IncludeSuper { get; init; }
    public bool IncludeUserdata { get; init; }

    public event Action<string, double>? Progress;

    public async Task<BackupManifest> RunAsync(string destDir, CancellationToken ct)
    {
        var table = edl.Table ?? throw new InvalidOperationException("not connected");
        Directory.CreateDirectory(destDir);
        var todo = table.All.Where(p => (IncludeUserdata || p.Label != "userdata") && (IncludeSuper || p.Label != "super")).ToList();
        var total = todo.Sum(p => (double)p.Sectors);
        double done = 0;

        var parts = new List<BackupEntry>();
        foreach (var p in todo)
        {
            Progress?.Invoke(p.Label, done / total);
            var file = $"lun{p.Lun}_{p.Label}.img";
            await edl.ReadAsync(new EdlRead(p, file, p.Sectors), ct, destDir);
            var sha = await Download.Sha256FileAsync(Path.Combine(destDir, file), ct);
            parts.Add(new BackupEntry(p.Lun, p.Label, p.FirstSector, p.Sectors, file, sha));
            log.Info($"backup {p.Label}: {p.SizeBytes(Gpt.SectorSize) >> 10} KiB {sha[..16]}");
            done += p.Sectors;
        }

        // Partition tables: the head (MBR + primary GPT) of every LUN. Enough to rebuild a damaged
        // table; the backup GPT at the end of each LUN is a copy of the same entries.
        var tables = new List<BackupEntry>();
        for (var lun = 0; lun < EdlSession.Luns; lun++)
        {
            var region = new GptPartition(lun, $"gpt_head_lun{lun}", 0, Gpt.HeadSectors - 1, 0);
            var file = $"lun{lun}_gpt_head.bin";
            await edl.ReadAsync(new EdlRead(region, file, Gpt.HeadSectors), ct, destDir);
            tables.Add(new BackupEntry(lun, region.Label, 0, Gpt.HeadSectors, file, await Download.Sha256FileAsync(Path.Combine(destDir, file), ct)));
        }

        string? slot = null;
        try { slot = Install.InstallPlanner.ActiveSlot(table); } catch (InvalidDataException) { }
        var manifest = new BackupManifest(DateTime.Now, slot, edl.Unlock?.Unlocked ?? false, parts, tables);
        await File.WriteAllTextAsync(Path.Combine(destDir, "backup.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), ct);
        await File.WriteAllLinesAsync(Path.Combine(destDir, "SHA256SUMS"), parts.Concat(tables).Select(e => $"{e.Sha256}  {e.File}"), ct);
        Progress?.Invoke("done", 1);
        log.Info($"backup complete: {parts.Count} partitions in {destDir}");
        return manifest;
    }
}
