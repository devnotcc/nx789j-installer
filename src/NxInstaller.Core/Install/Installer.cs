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
        var images = Selected(entry.Manifest, o).ToList();
        for (var i = 0; i < images.Count; i++)
        {
            var idx = i;
            await ImageAssembler.AssembleAsync(entry, images[i], Path.Combine(AppPaths.Downloads, entry.Manifest.Id), dir, log,
                new Progress<double>(f => progress?.Report((idx + f) / images.Count)), ct);
        }
        return dir;
    }

    public static IEnumerable<PackageImage> Selected(PackageManifest m, InstallOptions o) =>
        m.Images.Where(i => i.Option is null || o.Options.Contains(i.Option));

    /// <param name="serial">adb serial of the phone if it is booted, or null if it is already in EDL.</param>
    public async Task InstallAsync(PackageEntry entry, InstallOptions o, string? serial, CancellationToken ct)
    {
        var m = entry.Manifest;
        if (!m.Enabled) throw new InvalidOperationException($"{m.Name} is not available yet: {m.DisabledReason}");

        Report("Preparing images", 0.0);
        var dir = await PrepareAsync(entry, o, ct, new Progress<double>(f => Step?.Invoke(new("Preparing images", f * 0.4))));
        var prepared = Selected(m, o).Select(i => new PreparedImage(i, Path.Combine(dir, i.File))).ToList();

        var edl = new EdlSession(tools, run, log, Path.Combine(AppPaths.Work, "edl")) { DryRun = o.DryRun };
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
                Report($"Checking {w.Partition.Label}", 0.8 + 0.15 * i++ / plan.Writes.Count);
                var back = await edl.ReadAsync(new EdlRead(w.Partition, "verify.bin", w.Sectors), ct);
                var want = prepared.First(p => p.LocalFile == w.File).Image.Sha256;
                var got = await Download.Sha256FileAsync(back, ct);
                File.Delete(back);
                if (got != want) throw new InvalidDataException($"{w.Partition.Label} reads back different from what was written. Do not restart the phone; try the install again.");
            }
        }

        Report("Restarting the phone", 0.97);
        await edl.ResetAsync(ct);
        Report(o.DryRun ? "Dry run finished: nothing was written" : $"{m.Name} installed", 1.0);
    }
}
