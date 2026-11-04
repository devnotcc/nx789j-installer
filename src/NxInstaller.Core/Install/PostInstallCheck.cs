using System.Text;
using NxInstaller.Core.Device;
using NxInstaller.Core.Linux;
using NxInstaller.Core.Packages;
using NxInstaller.Core.Safety;

namespace NxInstaller.Core.Install;

/// <summary>What an install wrote, for <see cref="PostInstallCheck"/> to compare the running phone against.</summary>
public sealed record InstallResult
{
    /// <summary>adb serial of the phone, when known (null when it was already in EDL).</summary>
    public string? Serial { get; init; }
    /// <summary>Slot suffix of the partitions written ("_a"/"_b"), or null when only unslotted ones were.</summary>
    public string? Slot { get; init; }
    /// <summary>Kernel banner ("Linux version ...") of the boot image written, or null when boot wasn't written.</summary>
    public string? KernelBanner { get; init; }
    /// <summary>Only boot and init_boot were written (the system is whatever was there).</summary>
    public bool KernelOnly { get; init; }
    public bool Wiped { get; init; }
    public bool RecoveryWritten { get; init; }
    /// <summary>Number of user-installed apps before the install (no-wipe installs), when it could be read.</summary>
    public int? UserAppsBefore { get; init; }
}

/// <summary>
/// After an install: waits for the phone to start and looks at what is actually running. Reads
/// only; nothing on the phone is changed. A Fail means the phone isn't running what was installed;
/// a Warn means it couldn't be confirmed (USB debugging not allowed yet, for example).
/// </summary>
public sealed class PostInstallCheck(Log log, Adb adb)
{
    public static TimeSpan BootTimeout { get; set; } = TimeSpan.FromMinutes(12);
    public static TimeSpan LinuxTimeout { get; set; } = TimeSpan.FromMinutes(8);
    /// <summary>How long the phone has to stay up after starting, to catch a boot loop.</summary>
    public static TimeSpan StableFor { get; set; } = TimeSpan.FromSeconds(45);

    public event Action<string>? Status;
    void Say(string s) { log.Info(s); Status?.Invoke(s); }

    public async Task<List<Check>> CheckAndroidAsync(PackageManifest m, InstallResult r, CancellationToken ct)
    {
        var checks = new List<Check>();
        var end = DateTime.UtcNow + BootTimeout;

        Say("Waiting for the phone to start");
        var serial = await WaitForDeviceAsync(r.Serial, end, ct);
        if (serial is null)
        {
            checks.Add(new("Phone started", CheckState.Warn,
                r.Wiped
                    ? "The phone didn't come back on USB debugging. That's expected after an erase: if it shows the setup screen, it started; turn on USB debugging and check again to confirm."
                    : "The phone didn't come back on USB, so the install couldn't be checked. If it shows the lock screen, it started."));
            return checks;
        }
        if (serial.StartsWith('!'))
        {
            checks.Add(new("Phone started", CheckState.Warn, serial[1..]));
            return checks;
        }

        Say("Waiting for Android to finish starting");
        if (!await WaitForBootAsync(serial, end, ct))
        {
            checks.Add(new("Phone started", CheckState.Fail,
                $"Android didn't finish starting within {BootTimeout.TotalMinutes:0} minutes. If it's stuck on the boot animation, put it in EDL mode and install again."));
            return checks;
        }
        var up0 = await UptimeAsync(serial, ct);
        checks.Add(new("Phone started", CheckState.Pass, $"Android started (up {up0:0} s)."));

        var p = await adb.PropsAsync(serial, ct);
        string? P(string k) => p.TryGetValue(k, out var v) && v.Length > 0 ? v : null;

        var model = P("ro.product.device") ?? P("ro.product.vendor.device");
        checks.Add(m.Compat.Models.Contains(model ?? "")
            ? new("Model", CheckState.Pass, model!)
            : new("Model", CheckState.Fail, $"The phone reports {model ?? "no model"}, not {string.Join(" or ", m.Compat.Models)}."));

        var (os, ver) = DeviceProbe.DetectOs(p);
        var family = FamilyOf(os);
        if (!r.KernelOnly && m.Family is not null)
            checks.Add(family == m.Family
                ? new("System", CheckState.Pass, $"{m.Name} {ver} is running.")
                : new("System", CheckState.Fail, $"The phone runs {(os == PhoneOs.Unknown ? "an unknown system" : os.ToString())} {ver}, not {m.Name}."));

        if (!r.KernelOnly && m.ExpectProps.Count > 0)
        {
            var wrong = m.ExpectProps.Where(kv => P(kv.Key) != kv.Value).Select(kv => $"{kv.Key} = {P(kv.Key) ?? "(unset)"}, expected {kv.Value}").ToList();
            checks.Add(wrong.Count == 0
                ? new("Build", CheckState.Pass, $"This is the {m.Version} build ({string.Join(", ", m.ExpectProps.Select(kv => $"{kv.Key} {kv.Value}"))}).")
                : new("Build", CheckState.Fail, $"The phone isn't running the {m.Version} build: {string.Join("; ", wrong)}."));
        }

        if (r.Slot is not null)
        {
            var slot = P("ro.boot.slot_suffix");
            checks.Add(slot == r.Slot
                ? new("Slot", CheckState.Pass, $"Started from slot {r.Slot}, where the install went.")
                : new("Slot", CheckState.Fail, $"Started from slot {slot ?? "?"}, but the install went to {r.Slot}: the bootloader switched slots, so the new system didn't start."));
        }

        if (r.KernelBanner is not null)
        {
            var running = (await adb.ShellAsync(serial, "cat /proc/version", ct)).Trim();
            checks.Add(running.StartsWith(r.KernelBanner, StringComparison.Ordinal)
                ? new("Kernel", CheckState.Pass, KernelRelease(running))
                // boot read back correctly, so a different answer is usually a root-hiding module
                // reporting the stock kernel; only a warning
                : new("Kernel", CheckState.Warn, $"The kernel reports {KernelRelease(running)}, not the installed {KernelRelease(r.KernelBanner)}. A root-hiding module that spoofs the kernel version does this; otherwise the new kernel didn't start."));
        }

        var se = (await adb.ShellAsync(serial, "getenforce", ct)).Trim();
        checks.Add(se == "Enforcing"
            ? new("SELinux", CheckState.Pass, "Enforcing.")
            : new("SELinux", CheckState.Warn, $"{(se.Length > 0 ? se : "Unknown")}: apps that check the phone's integrity may refuse to run."));

        if (r.Wiped)
            checks.Add(new("Data", CheckState.Pass, "The phone was erased; it starts with the setup screen."));
        else if (r.UserAppsBefore is { } before)
        {
            var after = await UserAppsAsync(serial, ct);
            checks.Add(after is null ? new("Apps and data", CheckState.Warn, "Couldn't list the installed apps.")
                : after >= before ? new("Apps and data", CheckState.Pass, $"{after} installed apps, as before.")
                : new("Apps and data", CheckState.Warn, $"{after} installed apps; there were {before} before the install."));
        }

        if (r.RecoveryWritten)
            checks.Add(new("Recovery", CheckState.Pass, "TWRP was written and read back; it starts with Volume Up + Power."));

        // a phone that restarts by itself shortly after starting is in a boot loop
        Say("Checking that the phone stays up");
        await Task.Delay(StableFor, ct);
        var up1 = await UptimeAsync(serial, ct);
        checks.Add(up1 is { } u1 && up0 is { } u0 && u1 > u0
            ? new("Stable", CheckState.Pass, $"Still running {StableFor.TotalSeconds:0} s later.")
            : new("Stable", CheckState.Fail, "The phone restarted by itself right after starting."));

        foreach (var c in checks) log.Info($"check: {c.Name}: {c.State}: {c.Detail}");
        return checks;
    }

    /// <summary>After a Linux install that restarted into Linux: systemd and the desktop must come up.</summary>
    public async Task<List<Check>> CheckLinuxAsync(string serial, CancellationToken ct)
    {
        var checks = new List<Check>();
        var end = DateTime.UtcNow + LinuxTimeout;
        Say("Waiting for Linux to start");
        var s = await WaitForDeviceAsync(serial, end, ct);
        if (s is null || s.StartsWith('!'))
        {
            checks.Add(new("Phone started", CheckState.Warn, s?[1..] ?? "The phone didn't come back on USB, so Linux couldn't be checked. If the screen shows the Plasma desktop, it worked."));
            return checks;
        }
        if (!await WaitForBootAsync(s, end, ct))
        {
            checks.Add(new("Phone started", CheckState.Fail, "Android's side (drivers and services Linux runs on) didn't finish starting."));
            return checks;
        }
        checks.Add(new("Phone started", CheckState.Pass, "The phone started."));

        var root = await adb.SuAsync(s, "id -u", ct);
        if (root?.Trim() != "0")
        {
            checks.Add(new("Root", CheckState.Warn, "Shell has no root after the restart, so Linux can't be looked at. Check the screen: it should show the Plasma desktop."));
            return checks;
        }

        var mode = (await adb.SuAsync(s, $"cat {LinuxAgent.InstallDir}/bootmode", ct))?.Trim();
        checks.Add(mode is "linux" or "linux-once"
            ? new("Boot mode", CheckState.Pass, $"Starts {mode}.")
            : new("Boot mode", CheckState.Fail, $"The boot mode is \"{mode}\", so Linux wasn't started."));

        // systemd first, then the desktop; the first start grows the disk and takes ~3 minutes
        string? state = null; bool desktop = false;
        while (DateTime.UtcNow < end)
        {
            state = (await adb.SuAsync(s, LinuxStateCommand, ct))?.Trim();
            desktop = (await adb.SuAsync(s, "pidof kwin_wayland >/dev/null && pidof plasmashell >/dev/null && echo yes", ct))?.Trim() == "yes";
            if (state is "running" or "degraded" && desktop) break;
            Say(state is null or "" or "no-systemd" ? "Waiting for Linux to start" : $"Linux is starting ({state})");
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
        checks.Add(state switch
        {
            "running" => new("Linux", CheckState.Pass, "systemd is up and every service started."),
            "degraded" => new("Linux", CheckState.Warn, "systemd is up, but a service failed to start (systemctl --failed lists it)."),
            "no-systemd" or null or "" => new("Linux", CheckState.Fail, "systemd didn't start. The log on the phone is /data/nx-linux/boot.log."),
            _ => new("Linux", CheckState.Fail, $"systemd is still \"{state}\" after {LinuxTimeout.TotalMinutes:0} minutes."),
        });
        checks.Add(desktop
            ? new("Desktop", CheckState.Pass, "KDE Plasma is running.")
            : new("Desktop", CheckState.Fail, "The Plasma desktop didn't start."));

        var se = (await adb.ShellAsync(s, "getenforce", ct)).Trim();
        checks.Add(se == "Enforcing"
            ? new("Android", CheckState.Pass, "Android still runs with root and SELinux enforcing.")
            : new("Android", CheckState.Warn, $"SELinux is {se}."));

        foreach (var c in checks) log.Info($"check: {c.Name}: {c.State}: {c.Detail}");
        return checks;
    }

    /// <summary>Prints systemd's state inside the Linux namespaces, or "no-systemd".</summary>
    internal static string LinuxStateCommand =>
        $"B={LinuxAgent.InstallDir}; p=$(cat $B/systemd.pid 2>/dev/null); "
        + "[ -n \"$p\" ] && [ \"$(cat /proc/$p/comm 2>/dev/null)\" = systemd ] || { echo no-systemd; exit 0; }; "
        + $"$B/{LinuxAgent.Busybox} nsenter -t $p -m -u -i -p -n systemctl is-system-running 2>/dev/null";

    /// <summary>
    /// Waits for the one USB phone to be back on adb as a booted device. Returns its serial, null on
    /// timeout, or "!reason" when it is there but can't be checked (unauthorized, in recovery).
    /// </summary>
    async Task<string?> WaitForDeviceAsync(string? serial, DateTime end, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), ct);
        var toldAuth = false;
        string? last = null;
        while (DateTime.UtcNow < end)
        {
            ct.ThrowIfCancellationRequested();
            var usb = (await adb.DevicesAsync(ct)).Where(d => d.IsUsb && (serial is null || d.Serial == serial)).ToList();
            if (usb.Count == 1)
            {
                var d = usb[0];
                if (d.State == "device") return d.Serial;
                if (d.State == "unauthorized" && !toldAuth)
                {
                    toldAuth = true;
                    Say("Allow USB debugging in the prompt on the phone's screen so the install can be checked");
                }
                last = d.State;
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        return last switch
        {
            "unauthorized" => "!The phone started, but USB debugging wasn't allowed on its screen, so the install couldn't be checked.",
            "recovery" => "!The phone started into recovery instead of the system.",
            _ => null,
        };
    }

    async Task<bool> WaitForBootAsync(string serial, DateTime end, CancellationToken ct)
    {
        while (DateTime.UtcNow < end)
        {
            ct.ThrowIfCancellationRequested();
            if ((await adb.ShellAsync(serial, "getprop sys.boot_completed", ct)).Trim() == "1") return true;
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        return false;
    }

    async Task<double?> UptimeAsync(string serial, CancellationToken ct) =>
        double.TryParse((await adb.ShellAsync(serial, "cat /proc/uptime", ct)).Split(' ')[0],
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var u) ? u : null;

    public async Task<int?> UserAppsAsync(string serial, CancellationToken ct)
    {
        var o = await adb.ShellAsync(serial, "pm list packages -3 2>/dev/null", ct);
        return o.Contains("package:") || o.Length == 0 ? o.Split('\n').Count(l => l.StartsWith("package:")) : null;
    }

    public static string? FamilyOf(PhoneOs os) => os switch
    {
        PhoneOs.EvolutionX => "evolutionx", PhoneOs.LineageOS => "lineageos", PhoneOs.Debian => "debian", _ => null,
    };

    /// <summary>"6.6.92-android15-8-g..." out of a "Linux version ..." banner.</summary>
    internal static string KernelRelease(string banner)
    {
        var f = banner.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return f.Length >= 3 && f[0] == "Linux" && f[1] == "version" ? f[2] : banner;
    }

    /// <summary>
    /// The "Linux version ..." banner of the kernel in a boot image (header v3/v4: kernel after one
    /// 4 KiB page), or null when the kernel is compressed or there is none.
    /// </summary>
    public static string? KernelBannerOf(string bootImage)
    {
        using var f = File.OpenRead(bootImage);
        var head = new byte[12];
        if (f.Read(head, 0, 12) != 12 || Encoding.ASCII.GetString(head, 0, 8) != "ANDROID!") return null;
        var size = BitConverter.ToInt32(head, 8);
        if (size <= 0) return null;
        var kernel = new byte[size];
        f.Position = 4096;
        if (f.ReadAtLeast(kernel, size, false) != size) return null;
        var marker = "Linux version "u8;
        var i = kernel.AsSpan().IndexOf(marker);
        if (i < 0) return null;
        var j = i;
        while (j < kernel.Length && kernel[j] != 0 && kernel[j] != (byte)'\n') j++;
        return Encoding.ASCII.GetString(kernel, i, j - i).Trim();
    }
}
