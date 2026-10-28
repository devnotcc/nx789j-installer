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
            ? new("Model", CheckState.Pass, $"{model} (REDMAGIC 10 Pro)")
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
                list.Add(new("Firmware", CheckState.Fail,
                    $"Firmware {s.Baseband} is not one these images were tested on. Installing could leave the phone in a boot loop."));
        }

        // Stock Android tells the truth about the lock state; the real check is in EDL anyway.
        if (s.Os == PhoneOs.Stock && s.VerifiedBootState == "green")
            list.Add(new("Bootloader", CheckState.Fail, "The bootloader is locked. Unlock it first; installing on a locked phone stops it from booting."));
        else if (s.Mode == PhoneMode.Fastboot && s.FastbootUnlocked == "no")
            list.Add(new("Bootloader", CheckState.Fail, "The bootloader reports it is locked. Unlock it first."));
        else
            list.Add(new("Bootloader", CheckState.Pass, "Checked again in EDL mode before anything is written."));

        if (s.Os == PhoneOs.Stock)
            list.Add(new("Stock firmware", CheckState.Warn,
                "Keep the phone offline while it runs stock: a ZTE update can patch the bootloader unlock and there is no way back."));
        return list;
    }

    /// <summary>The check that gates writing: the bootloader's own devinfo, read over EDL.</summary>
    public static Check CheckEdl(DevInfo info) => info.Unlocked
        ? new("Bootloader", CheckState.Pass, "Unlocked (read from devinfo)")
        : new("Bootloader", CheckState.Fail, "Locked (read from devinfo). Nothing will be written.");

    public static bool Blocks(this IEnumerable<Check> checks) => checks.Any(c => c.State == CheckState.Fail);
}
