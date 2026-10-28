using NxInstaller.Core.Packages;

namespace NxInstaller.App.Services;

/// <summary>
/// The choices on the "Choose what to install" page. The list is fixed so the page looks the same
/// before packages are published; each entry is matched to a package from the current source by
/// id, and shows as unavailable when there is none (or when the package itself is disabled).
/// </summary>
public sealed record CatalogItem(string Id, string Title, string Subtitle, string Glyph, string? Variant, bool ComingSoon)
{
    public PackageEntry? Package { get; set; }

    public bool Available => !ComingSoon && Package is { Manifest.Enabled: true };

    public string Status =>
        ComingSoon ? "Coming soon"
        : Package is null ? "Not published yet"
        : !Package.Manifest.Enabled ? Package.Manifest.DisabledReason ?? "Not available"
        : $"Version {Package.Manifest.Version}";
}

public static class Catalog
{
    public static List<CatalogItem> Create() =>
    [
        new("evox", "Evolution X", "Android 16 with Google apps, Pixel features and the RedMagic extras (fan, slider, performance modes).", "\uE8EA", null, false),
        new("lineage-gapps", "LineageOS", "Clean, close-to-stock Android 16, with or without Google apps.", "\uE8EA", "With Google apps", true),
        new("lineage-vanilla", "LineageOS", "Clean, close-to-stock Android 16, with or without Google apps.", "\uE8EA", "No Google apps", true),
        new("debian-plasma", "Debian 13 with KDE Plasma", "A full Linux desktop on the phone's own hardware: GPU acceleration, Wi-Fi, sound, fan control.", "\uE7F8", null, false),
        new("twrp", "TWRP recovery only", "Replace the recovery with TWRP without changing the installed OS.", "\uE90F", null, false),
    ];
}
