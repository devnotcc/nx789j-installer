namespace NxInstaller.Core;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// One log for a whole run. Every line goes to a file under %LOCALAPPDATA%\NxInstaller\logs and to
/// whoever subscribes (the UI's log view, the CLI's console). Users attach this file to bug reports,
/// so it records the commands run and their output, never key material.
/// </summary>
public sealed class Log : IDisposable
{
    readonly StreamWriter? _file;
    readonly object _lock = new();

    public event Action<LogLevel, string>? Line;
    public string? FilePath { get; }

    public Log(string? directory = null)
    {
        directory ??= Path.Combine(AppPaths.Root, "logs");
        try
        {
            Directory.CreateDirectory(directory);
            FilePath = Path.Combine(directory, $"run-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            _file = new StreamWriter(FilePath, append: true) { AutoFlush = true };
        }
        catch (IOException) { /* logging to the event only */ }
    }

    public void Write(LogLevel level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {level.ToString().ToUpperInvariant(),-5} {message}";
        lock (_lock) _file?.WriteLine(line);
        Line?.Invoke(level, message);
    }

    public void Debug(string m) => Write(LogLevel.Debug, m);
    public void Info(string m) => Write(LogLevel.Info, m);
    public void Warn(string m) => Write(LogLevel.Warn, m);
    public void Error(string m) => Write(LogLevel.Error, m);

    public void Dispose() => _file?.Dispose();
}

public static class AppPaths
{
    public static string Root { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NxInstaller");

    public static string Tools => Path.Combine(Root, "tools");
    public static string Downloads => Path.Combine(Root, "downloads");
    public static string Work => Path.Combine(Root, "work");
    public static string Settings => Path.Combine(Root, "settings.json");

    /// <summary>
    /// Unpacked images are big (super 16 GiB): keep only the version being installed in
    /// work/&lt;id&gt;/ and delete the others (the packages themselves are not touched).
    /// </summary>
    public static void PruneOtherVersions(string id, string keepVersion, Log? log = null)
    {
        var dir = Path.Combine(Work, id);
        if (!Directory.Exists(dir)) return;
        foreach (var d in Directory.GetDirectories(dir))
        {
            if (string.Equals(Path.GetFileName(d), keepVersion, StringComparison.OrdinalIgnoreCase)) continue;
            try { Directory.Delete(d, true); log?.Info($"removed old unpacked images {d}"); }
            catch (IOException e) { log?.Warn($"could not remove {d}: {e.Message}"); }
            catch (UnauthorizedAccessException e) { log?.Warn($"could not remove {d}: {e.Message}"); }
        }
    }

    /// <summary>Free bytes on the drive holding <paramref name="path"/>.</summary>
    public static long FreeBytes(string path) =>
        new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
}
