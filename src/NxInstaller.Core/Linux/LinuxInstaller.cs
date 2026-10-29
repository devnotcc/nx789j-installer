using System.Text;
using NxInstaller.Core.Device;
using NxInstaller.Core.Edl;
using NxInstaller.Core.Install;
using NxInstaller.Core.Packages;

namespace NxInstaller.Core.Linux;

public sealed record LinuxOptions
{
    /// <summary>SHA-512 crypt hash of the password for the Linux account (<see cref="Sha512Crypt"/>).</summary>
    public required string PasswordHash { get; init; }
    /// <summary>Size of the Linux disk image on the phone (sparse). 0 = as shipped.</summary>
    public long ImageBytes { get; init; }
    /// <summary>Replace an installed Linux image (its files are lost).</summary>
    public bool ReplaceExisting { get; init; }
    /// <summary>Boot into Linux when done (bootmode linux); otherwise Android keeps starting.</summary>
    public bool BootLinux { get; init; } = true;
    public bool BackupFirst { get; init; } = true;
    /// <summary>Plan and check everything, upload nothing and write nothing.</summary>
    public bool DryRun { get; init; }
}

/// <summary>
/// Installs a Linux package on top of an installed Android: uploads the image and its launchers to
/// the phone over adb, writes the package's kernel (boot + init_boot of the active slot) over EDL,
/// and lets the install agent in that init_boot move the upload into place on the next boot.
/// Then it sets the boot mode the same way and restarts into Linux. Android's data is kept.
/// </summary>
public sealed class LinuxInstaller(Log log, ProcessRunner run, Adb adb, EdlTools tools)
{
    /// <summary>Package ids a Linux package can name in "requires", and the OS family each one is.</summary>
    public static readonly IReadOnlyDictionary<string, string> AndroidBases = new Dictionary<string, string>
    {
        ["evox"] = "evolutionx", ["lineage-gapps"] = "lineageos", ["lineage-vanilla"] = "lineageos",
    };

    /// <summary>Room the installer keeps free on /data beyond the upload and the image's growth.</summary>
    public const long Headroom = 6L << 30;

    public event Action<InstallStep>? Step;
    void Report(string title, double p) { log.Info(title); Step?.Invoke(new InstallStep(title, p)); }

    /// <summary>Why this phone cannot take the package, or null when it can.</summary>
    public static string? CheckBase(PackageManifest m, string? currentFamily)
    {
        if (m.Kind != PackageKind.Linux) return $"{m.Name} is not a Linux package";
        if (m.Requires.Count == 0) return null;
        var families = m.Requires.Select(r => AndroidBases.GetValueOrDefault(r)).Where(f => f is not null).ToHashSet();
        return currentFamily is not null && families.Contains(currentFamily) ? null
            : $"{m.Name} is installed on top of Android ({string.Join(" or ", m.Requires)}). Install that first, start it once, then come back.";
    }

    public async Task InstallAsync(PackageEntry entry, LinuxOptions o, string serial, string? currentFamily, CancellationToken ct)
    {
        var m = entry.Manifest;
        if (!m.Enabled) throw new InvalidOperationException($"{m.Name} is not available yet: {m.DisabledReason}");
        if (CheckBase(m, currentFamily) is { } why) throw new InvalidOperationException(why);
        var firstboot = LinuxAgent.FirstbootFor(o.PasswordHash);

        // 1. everything on the PC first
        Report("Preparing files", 0.0);
        var dir = Path.Combine(AppPaths.Work, m.Id, m.Version);
        var local = new List<(PackageFile File, string Path)>();
        for (var i = 0; i < m.Files.Count; i++)
        {
            var idx = i;
            var path = await ImageAssembler.AssembleAsync(entry, m.Files[i], Path.Combine(AppPaths.Downloads, m.Id), dir, log,
                new Progress<double>(f => Step?.Invoke(new("Preparing files", (idx + f) / m.Files.Count * 0.2))), ct);
            local.Add((m.Files[i], path));
        }
        var image = local.Single(l => l.File.File == LinuxAgent.Image);
        var grow = o.ImageBytes > image.File.Size ? o.ImageBytes / 4096 * 4096 : (long?)null;

        // 2. what is on the phone
        Report("Checking the phone's storage", 0.21);
        var existing = await adb.ShellAsync(serial, $"ls {LinuxAgent.InstallDir}/{LinuxAgent.Image} 2>&1", ct);
        if (existing.Trim() == $"{LinuxAgent.InstallDir}/{LinuxAgent.Image}" && !o.ReplaceExisting)
            throw new InvalidOperationException("Linux is already installed on this phone. Choose to replace it (its files are deleted) to install again.");
        var avail = LinuxAgent.ParseDfAvailable(await adb.ShellAsync(serial, "df -k /data", ct))
                    ?? throw new InvalidDataException("could not read the free space on the phone");
        var upload = local.Sum(l => l.File.Size);
        var growth = (grow ?? image.File.Size) - image.File.Size;
        log.Info($"phone: {avail >> 30} GiB free; upload {upload >> 20} MiB, image {(grow ?? image.File.Size) >> 30} GiB");
        if (upload + growth + Headroom > avail)
            throw new InvalidOperationException($"Not enough free space on the phone: {(upload + growth + Headroom) >> 30} GiB needed, {avail >> 30} GiB free. Choose a smaller Linux disk or free up space.");

        // 3. the upload (the agent installs it on the first boot of the new kernel)
        var files = new List<(string Name, string Path)>(local.Select(l => (l.File.File, l.Path)));
        var fbPath = Path.Combine(dir, LinuxAgent.FirstbootConf);
        await File.WriteAllTextAsync(fbPath, firstboot, new UTF8Encoding(false), ct);
        files.Add((LinuxAgent.FirstbootConf, fbPath));
        try
        {
            if (o.DryRun) log.Info($"dry run: would upload {string.Join(", ", files.Select(f => f.Name))}");
            else await UploadAsync(serial, files, grow, o.ReplaceExisting, 0.22, 0.4, ct);
        }
        catch
        {
            // cancelled or failed half-way: nothing half-uploaded stays on the phone
            try { await adb.ShellAsync(serial, $"rm -rf {LinuxAgent.RemoteDir}", CancellationToken.None); } catch { }
            throw;
        }
        finally { File.Delete(fbPath); }

        // 4. the kernel with the install agent
        var kernel = new Installer(log, run, adb, tools);
        kernel.Step += st => Step?.Invoke(st with { Progress = 0.4 + st.Progress * 0.35 });
        try
        {
            await kernel.InstallAsync(entry, new InstallOptions { BackupFirst = o.BackupFirst, DryRun = o.DryRun, Wipe = false }, serial, ct);
        }
        catch when (!o.DryRun)
        {
            // the phone may still be in Android (failure before EDL); leave nothing half-uploaded
            try { await adb.ShellAsync(serial, $"rm -rf {LinuxAgent.RemoteDir}", CancellationToken.None); } catch { }
            throw;
        }
        if (o.DryRun) { Report("Dry run finished: nothing was uploaded or written", 1.0); return; }

        // 5. first boot of the new kernel: the agent installs the upload
        Report("Waiting for the phone to start (it installs Linux on the way)", 0.76);
        var st1 = await WaitForStatusAsync(serial, TimeSpan.FromMinutes(20), ct);
        if (!st1.Ok) throw new InvalidOperationException($"The phone refused the Linux files: {st1.Reason}");
        foreach (var d in st1.Details) log.Info($"phone: {d}");

        // 6. boot mode, then restart into Linux
        if (o.BootLinux)
        {
            Report("Switching the phone to start Linux", 0.9);
            var bm = Path.Combine(dir, LinuxAgent.BootMode);
            await File.WriteAllTextAsync(bm, "linux\n", new UTF8Encoding(false), ct);
            try { await UploadAsync(serial, [(LinuxAgent.BootMode, bm)], null, false, 0.9, 0.92, ct); }
            finally { File.Delete(bm); }
            await adb.RunAsync(serial, ["reboot"], TimeSpan.FromSeconds(30), ct);
            var st2 = await WaitForStatusAsync(serial, TimeSpan.FromMinutes(10), ct);
            if (!st2.Ok) throw new InvalidOperationException($"The phone refused the boot mode: {st2.Reason}");
        }
        Report(o.BootLinux ? $"{m.Name} is installed and starting" : $"{m.Name} is installed (Android still starts by default)", 1.0);
    }

    async Task UploadAsync(string serial, IReadOnlyList<(string Name, string Path)> files, long? grow, bool replace, double p0, double p1, CancellationToken ct)
    {
        var r = LinuxAgent.RemoteDir;
        await adb.ShellAsync(serial, $"rm -rf {r} && mkdir -p {r}", ct);
        var uploads = new List<LinuxAgent.Upload>();
        long total = files.Sum(f => new FileInfo(f.Path).Length), done = 0;
        foreach (var (name, path) in files)
        {
            var size = new FileInfo(path).Length;
            Report($"Copying {name} to the phone ({Math.Max(1, size >> 20)} MiB)", p0 + (p1 - p0) * done / Math.Max(1, total));
            await adb.PushAsync(serial, path, $"{r}/{name}", ct);
            var onPhone = (await adb.ShellAsync(serial, $"stat -c %s {r}/{name}", ct)).Trim();
            if (onPhone != size.ToString()) throw new IOException($"{name}: {onPhone} bytes on the phone, {size} expected");
            uploads.Add(new LinuxAgent.Upload(name, size, await Download.Sha256FileAsync(path, ct)));
            done += size;
        }
        var man = Path.Combine(Path.GetDirectoryName(files[0].Path)!, "manifest");
        await File.WriteAllTextAsync(man, LinuxAgent.Manifest(uploads, grow, replace), new UTF8Encoding(false), ct);
        try { await adb.PushAsync(serial, man, $"{r}/manifest", ct); }
        finally { File.Delete(man); }
        // READY last: the agent only looks at a complete upload
        await adb.ShellAsync(serial, $"touch {r}/READY && ls {r}/READY", ct);
    }

    /// <summary>Waits until the phone is back on adb and the agent has written STATUS.</summary>
    async Task<AgentStatus> WaitForStatusAsync(string serial, TimeSpan timeout, CancellationToken ct)
    {
        var end = DateTime.UtcNow + timeout;
        var seen = false;
        while (DateTime.UtcNow < end)
        {
            ct.ThrowIfCancellationRequested();
            var d = (await adb.DevicesAsync(ct)).FirstOrDefault(x => x.Serial == serial);
            if (d?.State == "device")
            {
                if (!seen) { log.Info("phone is back on adb"); seen = true; }
                var text = await adb.ShellAsync(serial, $"cat {LinuxAgent.RemoteDir}/STATUS 2>/dev/null", ct);
                if (LinuxAgent.ParseStatus(text) is { } s) return s;
            }
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        throw new TimeoutException(seen
            ? "The phone started but did not report back from the install. Check /data/local/tmp/nx-install on it."
            : "The phone did not come back on USB. If it shows Android or Linux, the install may still have worked; check it and run the install again.");
    }
}
