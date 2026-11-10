using System.Text;
using NxInstaller.Core.Device;
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
    /// <summary>Start Linux once when done (bootmode linux-once): Android stays the default, so a
    /// restart from Android, a crash or a forced restart comes back to Android. Restarting from
    /// Linux stays in Linux.</summary>
    public bool BootLinux { get; init; } = true;
    /// <summary>Make Linux the default instead (bootmode linux): every boot starts Linux until
    /// "Restart into Android" (one boot) or the boot hook's fallback after failed boots.</summary>
    public bool LinuxDefault { get; init; }
    /// <summary>Plan and check everything, upload nothing and write nothing.</summary>
    public bool DryRun { get; init; }
}

/// <summary>
/// Installs a Linux package on top of an installed Android: uploads the image and its launchers to
/// the phone over adb and runs the package's install agent as root through KernelSU, which moves
/// the upload into /data/nx-linux and installs the boot hook. Then it restarts into Linux.
/// Nothing is written to a partition: the OS's own kernel (KernelSU + the options Linux needs)
/// runs both. Android's data is kept.
/// </summary>
public sealed class LinuxInstaller(Log log, Adb adb)
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

    /// <summary>Why the running Android cannot install Linux (no root for adb, or a kernel without
    /// what Linux needs), or null when it can.</summary>
    public async Task<string?> CheckPhoneAsync(string serial, CancellationToken ct)
    {
        if (!await adb.HasRootAsync(serial, ct))
            return "Root isn't available to adb. In the KernelSU app, grant superuser to Shell, then try again.";
        var k = (await adb.SuAsync(serial, LinuxAgent.KernelCheck, ct))?.Trim();
        return k == "ok" ? null
            : $"The kernel on the phone can't run Linux ({k}). Install the current version of your Android first (it keeps your data), then try again.";
    }

    public async Task InstallAsync(PackageEntry entry, LinuxOptions o, string serial, string? currentFamily, CancellationToken ct)
    {
        var m = entry.Manifest;
        if (!m.Enabled) throw new InvalidOperationException($"{m.Name} is not available yet: {m.DisabledReason}");
        if (CheckBase(m, currentFamily) is { } why) throw new InvalidOperationException(why);
        if (!m.Files.Any(f => f.File == LinuxAgent.AgentScript) || !m.Files.Any(f => f.File == LinuxAgent.Busybox))
            throw new InvalidDataException($"{m.Name}: the package has no install agent; it needs a newer package");
        var firstboot = LinuxAgent.FirstbootFor(o.PasswordHash);

        // 1. everything on the PC first
        Report("Preparing files", 0.0);
        var dir = Path.Combine(AppPaths.Work, m.Id, m.Version);
        AppPaths.PruneOtherVersions(m.Id, m.Version, log);
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
        Report("Checking the phone", 0.21);
        if (await CheckPhoneAsync(serial, ct) is { } notReady) throw new InvalidOperationException(notReady);
        var existing = await adb.SuAsync(serial, $"ls {LinuxAgent.InstallDir}/{LinuxAgent.Image} 2>&1", ct) ?? "";
        if (existing.Trim() == $"{LinuxAgent.InstallDir}/{LinuxAgent.Image}" && !o.ReplaceExisting)
            throw new InvalidOperationException("Linux is already installed on this phone. Choose to replace it (its files are deleted) to install again.");
        var avail = LinuxAgent.ParseDfAvailable(await adb.ShellAsync(serial, "df -k /data", ct))
                    ?? throw new InvalidDataException("could not read the free space on the phone");
        var upload = local.Sum(l => l.File.Size);
        var growth = (grow ?? image.File.Size) - image.File.Size;
        log.Info($"phone: {avail >> 30} GiB free; upload {upload >> 20} MiB, image {(grow ?? image.File.Size) >> 30} GiB");
        if (upload + growth + Headroom > avail)
            throw new InvalidOperationException($"Not enough free space on the phone: {(upload + growth + Headroom) >> 30} GiB needed, {avail >> 30} GiB free. Choose a smaller Linux disk or free up space.");

        // 3. the upload, then the agent installs it right away
        var files = new List<(string Name, string Path)>(local.Select(l => (l.File.File, l.Path)));
        var fbPath = Path.Combine(dir, LinuxAgent.FirstbootConf);
        var bmPath = Path.Combine(dir, LinuxAgent.BootMode);
        await File.WriteAllTextAsync(fbPath, firstboot, new UTF8Encoding(false), ct);
        files.Add((LinuxAgent.FirstbootConf, fbPath));
        if (o.BootLinux || o.LinuxDefault)
        {
            await File.WriteAllTextAsync(bmPath, o.LinuxDefault ? "linux\n" : "linux-once\n", new UTF8Encoding(false), ct);
            files.Add((LinuxAgent.BootMode, bmPath));
        }
        if (o.DryRun)
        {
            log.Info($"dry run: would upload {string.Join(", ", files.Select(f => f.Name))} and run the install agent as root");
            File.Delete(fbPath); File.Delete(bmPath);
            Report("Dry run finished: nothing was uploaded or written", 1.0);
            return;
        }
        try
        {
            await UploadAsync(serial, files, grow, o.ReplaceExisting, 0.22, 0.8, ct);
            Report("Installing the Linux files on the phone", 0.81);
            await adb.SuAsync(serial, LinuxAgent.RunCommand, ct);
        }
        catch
        {
            // failed or cancelled before the agent took the upload: leave no 6 GB behind
            try { await adb.ShellAsync(serial, $"rm -rf {LinuxAgent.RemoteDir}", CancellationToken.None); } catch { }
            throw;
        }
        finally
        {
            File.Delete(fbPath); File.Delete(bmPath);
        }
        var st = LinuxAgent.ParseStatus(await adb.ShellAsync(serial, $"cat {LinuxAgent.RemoteDir}/STATUS 2>/dev/null", ct));
        if (st is not { Ok: true })
        {
            // the agent consumes the upload; whatever is left of a refused or broken one goes too
            try { await adb.ShellAsync(serial, $"rm -f {LinuxAgent.RemoteDir}/{LinuxAgent.Image} {LinuxAgent.RemoteDir}/READY", CancellationToken.None); } catch { }
            throw new InvalidOperationException(st is null ? "The install agent on the phone did not report back." : $"The phone refused the Linux files: {st.Reason}");
        }
        foreach (var d in st.Details) log.Info($"phone: {d}");

        // 4. restart into Linux
        if (o.BootLinux || o.LinuxDefault)
        {
            Report("Restarting the phone into Linux", 0.9);
            await adb.RunAsync(serial, ["reboot"], TimeSpan.FromSeconds(30), ct);
            await WaitForAdbAsync(serial, TimeSpan.FromMinutes(5), ct);
        }
        Report(o.BootLinux || o.LinuxDefault ? $"{m.Name} is installed and starting" : $"{m.Name} is installed (Android still starts by default)", 1.0);
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
            // the agent runs from the upload but is not one of the files it installs
            if (name != LinuxAgent.AgentScript) uploads.Add(new LinuxAgent.Upload(name, size, await Download.Sha256FileAsync(path, ct)));
            done += size;
        }
        var man = Path.Combine(Path.GetDirectoryName(files[0].Path)!, "manifest");
        await File.WriteAllTextAsync(man, LinuxAgent.Manifest(uploads, grow, replace), new UTF8Encoding(false), ct);
        try { await adb.PushAsync(serial, man, $"{r}/manifest", ct); }
        finally { File.Delete(man); }
        // READY last: the agent only looks at a complete upload
        await adb.ShellAsync(serial, $"touch {r}/READY && ls {r}/READY", ct);
    }

    /// <summary>Waits until the restarting phone is back on adb (adb also runs on a Linux boot).</summary>
    async Task WaitForAdbAsync(string serial, TimeSpan timeout, CancellationToken ct)
    {
        var end = DateTime.UtcNow + timeout;
        await Task.Delay(TimeSpan.FromSeconds(10), ct);
        while (DateTime.UtcNow < end)
        {
            ct.ThrowIfCancellationRequested();
            var d = (await adb.DevicesAsync(ct)).FirstOrDefault(x => x.Serial == serial);
            if (d?.State == "device") { log.Info("phone is back on adb"); return; }
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        throw new TimeoutException("The phone did not come back on USB. If it shows Linux, the install worked.");
    }
}
