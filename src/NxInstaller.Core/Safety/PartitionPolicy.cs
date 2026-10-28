namespace NxInstaller.Core.Safety;

public enum WriteKind
{
    /// <summary>An OS image written over a partition.</summary>
    Image,
    /// <summary>Zeros over the start of metadata/userdata (a data wipe).</summary>
    Wipe,
}

/// <summary>
/// The one place that decides whether a partition may be written. Every write the engine makes is
/// checked here right before the XML goes to the phone, whatever a package manifest asks for.
///
/// The list is an allowlist: only the partitions an OS install legitimately replaces. Firmware
/// (bootloaders, TrustZone, modem, DSPs) and anything holding unlock or device-unique state is
/// never writable from this app. Those partitions are what make a phone recoverable, keep its
/// radio calibration, and carry the bootloader unlock, which on this phone comes from an exploit
/// that newer firmware has patched: losing it cannot be undone.
/// </summary>
public static class PartitionPolicy
{
    static readonly HashSet<string> ImageAllowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "super", "vbmeta", "vbmeta_system", "boot", "init_boot", "vendor_boot", "recovery", "dtbo",
    };

    static readonly HashSet<string> WipeAllowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "metadata", "userdata",
    };

    /// <summary>Named so error messages can say why, not just "not allowed".</summary>
    static readonly Dictionary<string, string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        ["xbl"] = "boot firmware", ["xbl_config"] = "boot firmware", ["xbl_ramdump"] = "boot firmware",
        ["abl"] = "bootloader (carries the unlock)", ["uefi"] = "boot firmware", ["imagefv"] = "boot firmware",
        ["tz"] = "TrustZone", ["hyp"] = "hypervisor", ["devcfg"] = "TrustZone config", ["keymaster"] = "TEE keystore",
        ["qupfw"] = "firmware", ["aop"] = "firmware", ["aop_config"] = "firmware", ["cpucp"] = "firmware",
        ["cpucp_dtb"] = "firmware", ["shrm"] = "firmware", ["uefisecapp"] = "TEE app", ["featenabler"] = "firmware",
        ["modem"] = "modem firmware", ["dsp"] = "DSP firmware", ["bluetooth"] = "Bluetooth firmware",
        ["spuservice"] = "secure processor", ["pvmfw"] = "VM firmware", ["vm-bootsys"] = "VM firmware",
        ["soccp_debug"] = "firmware", ["soccp_dcd"] = "firmware", ["pdp"] = "firmware", ["pdp_cdb"] = "firmware",
        ["devinfo"] = "bootloader unlock state", ["frp"] = "factory reset protection",
        ["storsec"] = "secure storage", ["secdata"] = "secure storage", ["ssd"] = "secure storage",
        ["persist"] = "device calibration", ["modemst1"] = "modem calibration", ["modemst2"] = "modem calibration",
        ["fsg"] = "modem calibration", ["fsc"] = "modem calibration", ["cdt"] = "board data", ["ddr"] = "RAM training",
        ["misc"] = "boot control", ["keystore"] = "keystore",
        ["qweslicstore"] = "licences", ["uefivarstore"] = "UEFI variables", ["ztecfg"] = "ZTE device config",
        ["vm-persist"] = "VM data", ["toolsfv"] = "firmware", ["qmcs"] = "firmware", ["tzsc"] = "TrustZone",
        ["spunvm"] = "secure processor", ["connsec"] = "secure storage",
    };

    /// <summary>"boot_a" -> "boot". Labels without a slot suffix are returned unchanged.</summary>
    public static string BaseName(string label) =>
        label.EndsWith("_a", StringComparison.OrdinalIgnoreCase) || label.EndsWith("_b", StringComparison.OrdinalIgnoreCase)
            ? label[..^2]
            : label;

    public static bool IsAllowed(string label, WriteKind kind, out string reason)
    {
        var b = BaseName(label);
        if (Forbidden.TryGetValue(b, out var what))
        {
            reason = $"{label} is {what}; this app never writes it";
            return false;
        }
        var ok = kind == WriteKind.Image ? ImageAllowed.Contains(b) : WipeAllowed.Contains(b);
        reason = ok ? "" : $"{label} is not on the list of partitions an install may {(kind == WriteKind.Image ? "write" : "wipe")}";
        return ok;
    }

    public static void Require(string label, WriteKind kind)
    {
        if (!IsAllowed(label, kind, out var reason)) throw new PolicyViolationException(reason);
    }
}

public sealed class PolicyViolationException(string message) : Exception(message);
