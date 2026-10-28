using System.IO.Compression;

namespace NxInstaller.Core.Device;

/// <summary>
/// Google's adb/fastboot. Downloaded from Google on first use into the app's folder instead of
/// being bundled, so users always get Google's current build under Google's own terms.
/// </summary>
public static class PlatformTools
{
    public const string Url = "https://dl.google.com/android/repository/platform-tools-latest-windows.zip";

    public static string Folder => Path.Combine(AppPaths.Tools, "platform-tools");
    public static string AdbExe => Path.Combine(Folder, "adb.exe");
    public static string FastbootExe => Path.Combine(Folder, "fastboot.exe");
    public static bool Installed => File.Exists(AdbExe) && File.Exists(FastbootExe);

    public static async Task EnsureAsync(HttpClient http, Log log, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (Installed) return;
        Directory.CreateDirectory(AppPaths.Tools);
        var zip = Path.Combine(AppPaths.Tools, "platform-tools.zip");
        log.Info("downloading Android platform-tools from Google");
        await Download.ToFileAsync(http, Url, zip, null, progress, ct);
        var tmp = Path.Combine(AppPaths.Tools, "platform-tools.tmp");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        ZipFile.ExtractToDirectory(zip, tmp);
        if (Directory.Exists(Folder)) Directory.Delete(Folder, true);
        Directory.Move(Path.Combine(tmp, "platform-tools"), Folder);
        Directory.Delete(tmp, true);
        File.Delete(zip);
        if (!Installed) throw new IOException("platform-tools archive did not contain adb.exe and fastboot.exe");
        log.Info("platform-tools installed");
    }
}
