using System.Security.Cryptography;
using NxInstaller.Core.Device;
using NxInstaller.Core.Edl;
using NxInstaller.Core.Packages;
using NxInstaller.Core.Safety;

namespace NxInstaller.Core.Install;

public sealed record InstallOptions
{
    /// <summary>Optional images to include, by their manifest "option" name (e.g. "twrp").</summary>
    public HashSet<string> Options { get; init; } = [];
    /// <summary>Format data. Decided by the wizard from <see cref="InstallPlanner.NeedsWipe"/> and the user's confirmation.</summary>
    public bool Wipe { get; init; }
    /// <summary>Save the partitions about to be replaced (not super) to the PC first.</summary>
    public bool BackupFirst { get; init; } = true;
    /// <summary>Read every written partition back and compare checksums.</summary>
    public bool Verify { get; init; } = true;
    public bool DryRun { get; init; }
    /// <summary>Write only these partitions of the package (e.g. boot + init_boot to put a kernel back). Null = all.</summary>
    public IReadOnlySet<string>? OnlyPartitions { get; init; }
}

public sealed record InstallStep(string Title, double Progress);

/// <summary>
/// Installs one package: prepare images on the PC, reboot to EDL, check the lock state from devinfo,
/// back up, write, verify, reboot. Nothing is written before every image is downloaded and verified
/// and the phone has been confirmed unlocked by the bootloader itself.
/// </summary>
public sealed class Installer(Log log, ProcessRunner run, Adb adb, EdlTools tools)
{
    public event Action<InstallStep>? Step;

    void Report(string title, double p) { log.Info(title); Step?.Invoke(new InstallStep(title, p)); }

    public async Task<string> PrepareAsync(PackageEntry entry, InstallOptions o, CancellationToken ct, IProgress<double>? progress = null)
    {
        var dir = Path.Combine(AppPaths.Work, entry.Manifest.Id, entry.Manifest.Version);
        AppPaths.PruneOtherVersions(entry.Manifest.Id, entry.Manifest.Version, log);
        var images = Selected(entry.Manifest, o).ToList();
        for (var i = 0; i < images.Count; i++)
        {
            var idx = i;
            await ImageAssembler.AssembleAsync(entry, images[i], Path.Combine(AppPaths.Downloads, entry.Manifest.Id), dir, log,
                new Progress<double>(f => progress?.Report((idx + f) / images.Count)), ct);
        }
        return dir;
    }

    /// <summary>Sectors read back per piece when checking a write (1 GiB).</summary>
    public const ulong VerifyChunkSectors = (1UL << 30) / Gpt.SectorSize;

    /// <summary>The pieces a read-back of <paramref name="sectors"/> sectors is split into: (offset, count).</summary>
    public static IEnumerable<(ulong Offset, ulong Sectors)> VerifyChunks(ulong sectors, ulong chunk = VerifyChunkSectors)
    {
        for (ulong o = 0; o < sectors; o += chunk) yield return (o, Math.Min(chunk, sectors - o));
    }

    /// <summary>
    /// Reads a written partition back piece by piece and returns the SHA-256 of all of it. Each piece is
    /// tried twice: a USB stall on one piece (seen on the 16 GiB super) costs one piece, not the whole
    /// check, and the PC never needs room for more than one piece. If a piece fails twice the firehose
    /// is usually hung, which only a reset of the phone clears.
    /// </summary>
    async Task<string> ReadBackSha256Async(EdlSession edl, GptPartition part, ulong sectors, Action<double> progress, CancellationToken ct)
    {
        var edlDir = Path.Combine(AppPaths.Work, "edl");
        var need = (long)Math.Min(sectors, VerifyChunkSectors) * Gpt.SectorSize + (512L << 20);
        var free = AppPaths.FreeBytes(edlDir);
        if (free < need)
            throw new IOException($"Not enough free space to check {part.Label}: it needs {need >> 20} MB on " +
                $"{Path.GetPathRoot(Path.GetFullPath(edlDir))}, {free >> 20} MB are free. Everything was written; " +
                "free up space and run the install again. Do not restart the phone.");
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[1 << 20];
        foreach (var (off, n) in VerifyChunks(sectors))
        {
            string back;
            try { back = await edl.ReadAsync(new EdlRead(part, "verify.bin", n, off), ct); }
            catch (EdlException first)
            {
                log.Warn($"reading {part.Label} back at sector {off} failed ({first.Message}); trying that piece again");
                try { back = await edl.ReadAsync(new EdlRead(part, "verify.bin", n, off), ct); }
                catch (EdlException e)
                {
                    throw new VerifyIncompleteException(part.Label, e);
                }
            }
            await using (var f = File.OpenRead(back))
            {
                int r;
                while ((r = await f.ReadAsync(buf, ct)) > 0) sha.AppendData(buf, 0, r);
            }
            File.Delete(back);
            progress((double)(off + n) / sectors);
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    public static IEnumerable<PackageImage> Selected(PackageManifest m, InstallOptions o) =>
        m.Images.Where(i => (i.Option is null || o.Options.Contains(i.Option))
                            && (o.OnlyPartitions is null || o.OnlyPartitions.Contains(i.Partition)));

    /// <summary>Options that rewrite only the kernel (boot + init_boot) of an installed package, keeping data.</summary>
    public static InstallOptions KernelOnly(bool dryRun) => new()
    {
        OnlyPartitions = new HashSet<string> { "boot", "init_boot" },
        Wipe = false,
        DryRun = dryRun,
    };

    /// <param name="serial">adb serial of the phone if it is booted, or null if it is already in EDL.</param>
    /// <returns>What was written, for <see cref="PostInstallCheck"/>; null after a dry run.</returns>
    public async Task<InstallResult?> InstallAsync(PackageEntry entry, InstallOptions o, string? serial, CancellationToken ct)
    {
        var m = entry.Manifest;
        if (!m.Enabled) throw new InvalidOperationException($"{m.Name} is not available yet: {m.DisabledReason}");

        Report("Preparing images", 0.0);
        var dir = await PrepareAsync(entry, o, ct, new Progress<double>(f => Step?.Invoke(new("Preparing images", f * 0.4))));
        var prepared = Selected(m, o).Select(i => new PreparedImage(i, Path.Combine(dir, i.File))).ToList();
        if (prepared.Count == 0) throw new InvalidOperationException($"{m.Name} has nothing to write with these options");

        var edl = new EdlSession(tools, run, log, Path.Combine(AppPaths.Work, "edl")) { DryRun = o.DryRun };
        int? appsBefore = null;
        if (serial is not null && !o.Wipe && !o.DryRun)
            try { appsBefore = await new PostInstallCheck(log, adb).UserAppsAsync(serial, ct); } catch (Exception e) when (e is not OperationCanceledException) { }
        if (serial is not null)
        {
            Report("Restarting the phone into EDL mode", 0.42);
            await adb.RunAsync(serial, ["reboot", "edl"], TimeSpan.FromSeconds(30), ct);
        }
        var port = await edl.WaitForPortAsync(TimeSpan.FromMinutes(2), ct);

        Report("Connecting in EDL mode", 0.45);
        await edl.ConnectAsync(port, ct);
        var unlock = DeviceGate.CheckEdl(edl.Unlock!);
        if (unlock.State == CheckState.Fail)
        {
            await edl.ResetAsync(ct);
            throw new PolicyViolationException(unlock.Detail);
        }

        var plan = InstallPlanner.Plan(edl.Table!, prepared, o.Wipe, n => InstallPlanner.ZeroFile(dir, n));
        foreach (var line in plan.Describe()) log.Info($"plan: {line}");

        if (o.BackupFirst)
        {
            Report("Saving the current boot partitions to this PC", 0.5);
            var bdir = Path.Combine(AppPaths.Root, "backups", $"before-{m.Id}-{DateTime.Now:yyyyMMdd-HHmmss}");
            foreach (var w in plan.Writes.Where(w => PartitionPolicy.BaseName(w.Partition.Label) != "super"))
                await edl.ReadAsync(new EdlRead(w.Partition, w.Partition.Label + ".img", w.Partition.Sectors), ct, bdir);
            log.Info($"backup saved in {bdir}");
        }

        Report($"Installing {m.Name}", 0.55);
        await edl.WriteAsync(plan.Writes.Concat(plan.Wipes).ToList(), ct);

        if (o.Verify && !o.DryRun)
        {
            var i = 0;
            foreach (var w in plan.Writes)
            {
                var p0 = 0.8 + 0.15 * i / plan.Writes.Count;
                var p1 = 0.8 + 0.15 * ++i / plan.Writes.Count;
                Report($"Checking {w.Partition.Label}", p0);
                var want = prepared.First(p => p.LocalFile == w.File).Image.Sha256;
                var got = await ReadBackSha256Async(edl, w.Partition, w.Sectors,
                    f => Step?.Invoke(new($"Checking {w.Partition.Label}", p0 + (p1 - p0) * f)), ct);
                if (got != want) throw new InvalidDataException($"{w.Partition.Label} reads back different from what was written. Do not restart the phone; try the install again.");
            }
        }

        Report("Restarting the phone", 0.97);
        await edl.ResetAsync(ct);
        Report(o.DryRun ? "Dry run finished: nothing was written" : $"{m.Name} installed", 1.0);
        if (o.DryRun) return null;

        var boot = prepared.FirstOrDefault(p => p.Image.Partition == "boot");
        string? banner = null;
        if (boot is not null) try { banner = PostInstallCheck.KernelBannerOf(boot.LocalFile); } catch (IOException) { }
        return new InstallResult
        {
            Serial = serial,
            Slot = plan.Writes.Select(w => w.Partition.Label).Select(l => l.EndsWith("_a") ? "_a" : l.EndsWith("_b") ? "_b" : null).FirstOrDefault(x => x is not null),
            KernelBanner = banner,
            KernelOnly = o.OnlyPartitions is { } only && !only.Contains("super"),
            Wiped = o.Wipe,
            RecoveryWritten = prepared.Any(p => p.Image.Partition == "recovery"),
            UserAppsBefore = appsBefore,
        };
    }
}

/// <summary>Everything was written, but reading it back stopped (the phone's EDL connection hung).</summary>
public sealed class VerifyIncompleteException(string partition, Exception inner) : IOException(
    $"Everything was written, but checking {partition} could not finish: the phone stopped answering in EDL mode. " +
    "Unplug the phone and hold Power + Volume Up + Volume Down for about 20 seconds until it restarts. " +
    "It should start normally (Android checks the system itself as it starts). If it does not, put it back " +
    "in EDL mode and run the install again: your data is kept.", inner)
{
    public string Partition { get; } = partition;
}
