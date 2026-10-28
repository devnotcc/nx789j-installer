using System.Text;

namespace NxInstaller.Core.Safety;

/// <summary>
/// The bootloader's own record of the lock state, read from the devinfo partition over EDL.
///
/// This is the check the installer trusts. Everything Android reports (ro.boot.verifiedbootstate,
/// ro.boot.flash.locked, even /proc/bootconfig) is commonly spoofed to "locked/green" by custom
/// ROMs and root-hiding modules so that banking apps and Play Integrity pass, so on a phone that is
/// set up the way this installer leaves it, those values say "locked" while the phone is unlocked.
///
/// Layout (Qualcomm ABL DeviceInfo): magic "ANDROID-BOOT!" (13 bytes), then is_unlocked,
/// is_unlock_critical, is_charger_screen_enabled, one byte each.
/// </summary>
public sealed record DevInfo(bool Unlocked, bool CriticalUnlocked)
{
    const string Magic = "ANDROID-BOOT!";

    public static DevInfo Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16 || Encoding.ASCII.GetString(data[..13]) != Magic)
            throw new InvalidDataException("devinfo has no ANDROID-BOOT! header; cannot tell the lock state");
        return new DevInfo(data[13] == 1, data[14] == 1);
    }
}
