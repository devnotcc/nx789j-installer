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
}
