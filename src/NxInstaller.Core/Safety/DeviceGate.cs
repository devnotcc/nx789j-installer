using NxInstaller.Core.Device;
using NxInstaller.Core.Packages;

namespace NxInstaller.Core.Safety;

public enum CheckState { Pass, Warn, Fail }

public sealed record Check(string Name, CheckState State, string Detail);

/// <summary>
/// The checks shown on the "Your phone" page before anything is written. A Fail blocks the install;
/// a Warn is shown and needs the user to continue. The final unlock check happens again in EDL,
/// right before writing (see <see cref="CheckEdl"/>), because Android's own answer can be spoofed.
/// </summary>
public static class DeviceGate
{
    public const int MinBattery = 30;

    public static List<Check> CheckPhone(PhoneSnapshot s, PackageManifest? m)
    {
        var list = new List<Check>();
        if (s.MoreThanOne)
        {
            list.Add(new("Phone", CheckState.Fail, "More than one phone is connected. Leave only the one to install on."));
            return list;
        }
        if (s.Mode == PhoneMode.None)
        {
            list.Add(new("Phone", CheckState.Fail, "No phone found. Connect it with a USB data cable and turn on USB debugging."));
            return list;
        }
        if (s.Mode == PhoneMode.Unauthorized)
        {
            list.Add(new("USB debugging", CheckState.Fail, "Allow USB debugging in the prompt on the phone's screen."));
            return list;
        }
        if (s.Mode == PhoneMode.Edl)
        {
            list.Add(new("Mode", CheckState.Pass, $"Phone is already in EDL mode (COM{s.EdlPort}); model and lock state are checked there."));
            return list;
        }

        var models = m?.Compat.Models ?? ["NX789J"];
        list.Add(s.Model is { } model && models.Contains(model)
            ? new("Model", CheckState.Pass, $"{model} (REDMAGIC 10 Pro / 10S Pro)")
            : new("Model", CheckState.Fail, $"This phone reports \"{s.Model ?? "unknown"}\". Only {string.Join(", ", models)} is supported; other models would be bricked."));

        if (s.Battery is { } b)
            list.Add(b >= MinBattery
                ? new("Battery", CheckState.Pass, $"{b}%")
                : new("Battery", CheckState.Fail, $"{b}%. Charge to at least {MinBattery}% first: a phone that dies while writing may not start again."));
        else
            list.Add(new("Battery", CheckState.Warn, $"Could not read the battery level. Make sure it is above {MinBattery}%."));

        if (m is { Compat.Basebands.Count: > 0 })
        {
            if (s.Baseband is null)
                list.Add(new("Firmware", CheckState.Warn, "Could not read the firmware version. The images were made for specific firmware."));
            else if (m.Compat.Basebands.Contains(s.Baseband))
                list.Add(new("Firmware", CheckState.Pass, s.Baseband));
            else
            {
                // Not a hard stop: the page offers to skip it (see NeedsFirmwareSkip), after these two
                list.Add(new("Firmware", CheckState.Warn,
                    $"Firmware {s.Baseband} isn't one these images were tested on. Tested: {TestedFirmware(m.Compat.Basebands)}."));
                list.Add(new(UntestedFirmware, CheckState.Warn,
                    "On other firmware the phone may not start (boot loop), or calls, mobile data and the SIM may stop working. " +
                    "Back up every partition first (Tools › Back up every partition) and only skip this check if you can restore the phone in EDL mode."));
            }
        }

        // Stock Android tells the truth about the lock state; the real check is in EDL anyway.
        if (s.Os == PhoneOs.Stock && s.VerifiedBootState == "green")
            list.Add(new("Bootloader", CheckState.Fail, "The bootloader is locked. Unlock it first; installing on a locked phone stops it from booting."));
        else if (s.Mode == PhoneMode.Fastboot && s.FastbootUnlocked == "no")
            list.Add(new("Bootloader", CheckState.Fail, "The bootloader reports it is locked. Unlock it first."));
        else
            list.Add(new("Bootloader", CheckState.Pass, "Checked again in EDL mode before anything is written."));

        if (m?.Kind == PackageKind.Linux)
        {
            // Linux goes on top of an installed Android, and its files are copied over adb
            if (s.Mode != PhoneMode.Android)
                list.Add(new("Android", CheckState.Fail, "Start the phone into Android: the Linux files are copied to it over USB debugging."));
            else if (Linux.LinuxInstaller.CheckBase(m, FamilyOf(s.Os)) is { } why)
                list.Add(new("Android", CheckState.Fail, why));
            else
                list.Add(new("Android", CheckState.Pass, $"{s.Os} {s.OsVersion}; its apps and data stay."));
        }

        if (s.Os == PhoneOs.Stock)
            list.Add(new("Stock firmware", CheckState.Warn,
                "Keep the phone offline while it runs stock: a ZTE update can patch the bootloader unlock and there is no way back."));
        return list;
    }

    /// <summary>The check that gates writing: the bootloader's own devinfo, read over EDL.</summary>
    public static Check CheckEdl(DevInfo info) => info.Unlocked
        ? new("Bootloader", CheckState.Pass, "Unlocked (read from devinfo)")
        : new("Bootloader", CheckState.Fail, "Locked (read from devinfo). Nothing will be written.");

    /// <summary>Package family of an installed OS (what manifests' wipe and requires rules name).</summary>
    public static string? FamilyOf(PhoneOs os) => os switch
    {
        PhoneOs.EvolutionX => "evolutionx", PhoneOs.LineageOS => "lineageos", PhoneOs.Debian => "debian", _ => null,
    };

    public static bool Blocks(this IEnumerable<Check> checks) => checks.Any(c => c.State == CheckState.Fail);

    /// <summary>Name of the warning that has to be confirmed before installing on untested firmware.</summary>
    public const string UntestedFirmware = "Untested firmware";

    /// <summary>Whether the user must confirm skipping the firmware check before continuing.</summary>
    public static bool NeedsFirmwareSkip(this IEnumerable<Check> checks) => checks.Any(c => c.Name == UntestedFirmware);

    /// <summary>RedMagicOS releases by the modem build their firmware reports (the part after "GEN_PACK-").</summary>
    static readonly Dictionary<string, string> RedMagicOs = new()
    {
        ["1.82720.133"] = "11.0.4",
        ["1.82720.138"] = "11.0.5",
    };

    /// <summary>"RedMagicOS 11.0.4 (…1.82720.133) and 11.0.5 (…1.82720.138)" for a package's firmware list.</summary>
    public static string TestedFirmware(IEnumerable<string> basebands)
    {
        var builds = basebands.Select(b => b[(b.LastIndexOf("GEN_PACK-", StringComparison.Ordinal) is var i and >= 0 ? i + 9 : 0)..])
            .Distinct().ToList();
        var named = builds.Select(b => RedMagicOs.TryGetValue(b, out var v) ? $"{v} (…{b})" : b).ToList();
        var text = named.Count switch
        {
            0 => "none listed",
            1 => named[0],
            _ => string.Join(", ", named.Take(named.Count - 1)) + " and " + named[^1],
        };
        return builds.Any(RedMagicOs.ContainsKey) ? "RedMagicOS " + text : text;
    }
}
