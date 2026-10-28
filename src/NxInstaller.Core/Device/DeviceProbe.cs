namespace NxInstaller.Core.Device;

public enum PhoneMode { None, Android, Recovery, Sideload, Unauthorized, Fastboot, Edl }

public enum PhoneOs { Unknown, Stock, EvolutionX, LineageOS, Twrp, Debian }

/// <summary>Everything the wizard shows about the connected phone, from one probe.</summary>
public sealed record PhoneSnapshot
{
    public PhoneMode Mode { get; init; }
    public string? Serial { get; init; }
    public string? Model { get; init; }
    public PhoneOs Os { get; init; }
    public string? OsVersion { get; init; }
    public string? Slot { get; init; }
    public int? Battery { get; init; }
    public bool Root { get; init; }
    public string? Kernel { get; init; }
    public string? Baseband { get; init; }
    public string? EdlPort { get; init; }
    /// <summary>What the bootloader says over fastboot ("yes"/"no"), when in fastboot mode.</summary>
    public string? FastbootUnlocked { get; init; }
    /// <summary>Only stock Android reports this honestly; custom ROMs and root hiding spoof it.</summary>
    public string? VerifiedBootState { get; init; }

    public bool IsSupportedModel => Model is "NX789J";
    public bool MoreThanOne { get; init; }
}

/// <summary>Finds the phone in whatever mode it is in and reads what can be read without changing anything.</summary>
public sealed class DeviceProbe(Adb adb, Fastboot fastboot)
{
    public async Task<PhoneSnapshot> ProbeAsync(CancellationToken ct = default)
    {
        var usb = (await adb.DevicesAsync(ct)).Where(d => d.IsUsb).ToList();
        if (usb.Count > 1) return new PhoneSnapshot { Mode = PhoneMode.None, MoreThanOne = true };
        if (usb.Count == 1) return await FromAdbAsync(usb[0], ct);

        var fb = await fastboot.DevicesAsync(ct);
        if (fb.Count > 1) return new PhoneSnapshot { Mode = PhoneMode.None, MoreThanOne = true };
        if (fb.Count == 1)
        {
            var s = fb[0];
            return new PhoneSnapshot
            {
                Mode = PhoneMode.Fastboot, Serial = s,
                Model = await fastboot.GetVarAsync(s, "product", ct),
                Slot = await fastboot.GetVarAsync(s, "current-slot", ct) is { } cs ? "_" + cs.TrimStart('_') : null,
                FastbootUnlocked = await fastboot.GetVarAsync(s, "unlocked", ct),
            };
        }

        return Edl.EdlSession.FindPort() is { } port
            ? new PhoneSnapshot { Mode = PhoneMode.Edl, EdlPort = port }
            : new PhoneSnapshot { Mode = PhoneMode.None };
    }

    async Task<PhoneSnapshot> FromAdbAsync(AdbDevice d, CancellationToken ct)
    {
        var mode = d.State switch
        {
            "device" => PhoneMode.Android,
            "recovery" => PhoneMode.Recovery,
            "sideload" => PhoneMode.Sideload,
            "unauthorized" => PhoneMode.Unauthorized,
            _ => PhoneMode.None,
        };
        if (mode is PhoneMode.Unauthorized or PhoneMode.Sideload or PhoneMode.None)
            return new PhoneSnapshot { Mode = mode, Serial = d.Serial };

        var p = await adb.PropsAsync(d.Serial, ct);
        string? P(string k) => p.TryGetValue(k, out var v) && v.Length > 0 ? v : null;

        var (os, ver) = mode == PhoneMode.Recovery
            ? (P("ro.twrp.version") is { } tw ? PhoneOs.Twrp : PhoneOs.Unknown, P("ro.twrp.version"))
            : DetectOs(p);

        int? battery = null;
        var dump = await adb.ShellAsync(d.Serial, "dumpsys battery 2>/dev/null | grep -m1 ' level:'", ct);
        if (int.TryParse(dump.Split(':').LastOrDefault()?.Trim(), out var lvl)) battery = lvl;
        else if (int.TryParse(await adb.ShellAsync(d.Serial, "cat /sys/class/power_supply/battery/capacity", ct), out lvl)) battery = lvl;

        return new PhoneSnapshot
        {
            Mode = mode, Serial = d.Serial,
            Model = P("ro.product.device") ?? P("ro.product.vendor.device"),
            Os = os, OsVersion = ver,
            Slot = P("ro.boot.slot_suffix"),
            Battery = battery,
            Root = mode == PhoneMode.Recovery || await adb.HasRootAsync(d.Serial, ct),
            Kernel = await adb.ShellAsync(d.Serial, "uname -r", ct),
            Baseband = P("gsm.version.baseband")?.Split(',')[0],
            VerifiedBootState = P("ro.boot.verifiedbootstate"),
        };
    }

    internal static (PhoneOs, string?) DetectOs(IReadOnlyDictionary<string, string> p)
    {
        string? P(string k) => p.TryGetValue(k, out var v) && v.Length > 0 ? v : null;
        if (P("ro.evolution.build.version") is { } ev) return (PhoneOs.EvolutionX, ev);
        if (p.Keys.Any(k => k.StartsWith("ro.evolution.", StringComparison.Ordinal))) return (PhoneOs.EvolutionX, P("ro.build.version.release"));
        if (P("ro.lineage.build.version") is { } lv) return (PhoneOs.LineageOS, lv);
        if (P("ro.build.display.id") is { } id && P("ro.build.fingerprint")?.StartsWith("nubia/", StringComparison.Ordinal) == true
            && P("ro.build.type") == "user" && P("ro.build.tags") == "release-keys" && id.Contains("REDMAGIC", StringComparison.OrdinalIgnoreCase))
            return (PhoneOs.Stock, id);
        return (PhoneOs.Unknown, P("ro.build.display.id"));
    }
}
