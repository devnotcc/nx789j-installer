namespace NxInstaller.Core.Device;

public sealed record AdbDevice(string Serial, string State, string Transport)
{
    /// <summary>"usb" for a cable, "tcp" for Wi-Fi debugging (ip:port serials).</summary>
    public bool IsUsb => Transport == "usb";
}

/// <summary>adb for one phone. Always passes -s, since a phone can be listed twice (USB and Wi-Fi).</summary>
public sealed class Adb(string exe, ProcessRunner run)
{
    public string Exe => exe;

    public async Task<List<AdbDevice>> DevicesAsync(CancellationToken ct = default)
    {
        var r = await run.RunAsync(exe, ["devices", "-l"], TimeSpan.FromSeconds(20), ct: ct, quiet: true);
        var list = new List<AdbDevice>();
        foreach (var line in r.StdOut.Split('\n').Skip(1))
        {
            var f = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 2) continue;
            var transport = f[0].Contains(':') || f[0].Contains("._adb-tls") ? "tcp" : "usb";
            list.Add(new AdbDevice(f[0], f[1], transport));
        }
        return list;
    }

    public Task<ProcessResult> RunAsync(string serial, IEnumerable<string> args, TimeSpan? timeout = null, CancellationToken ct = default) =>
        run.RunAsync(exe, ["-s", serial, .. args], timeout ?? TimeSpan.FromMinutes(2), ct: ct);

    public async Task<string> ShellAsync(string serial, string command, CancellationToken ct = default)
    {
        var r = await run.RunAsync(exe, ["-s", serial, "shell", command], TimeSpan.FromMinutes(2), ct: ct, quiet: true);
        return r.StdOut.Replace("\r", "").TrimEnd();
    }

    /// <summary>
    /// Runs a command as root through su (KernelSU/Magisk), or returns null when root is not
    /// available or not granted to the shell. The command is passed as one argument, so it is not
    /// re-split by the PC side.
    /// </summary>
    public async Task<string?> SuAsync(string serial, string command, CancellationToken ct = default)
    {
        var r = await run.RunAsync(exe, ["-s", serial, "shell", "su", "-c", Quote(command)], TimeSpan.FromMinutes(5), ct: ct, quiet: true);
        var output = r.StdOut.Replace("\r", "").TrimEnd();
        if (!r.Ok && (r.Output.Contains("not found") || r.Output.Contains("Permission denied") && output.Length == 0)) return null;
        return output;
    }

    public async Task<bool> HasRootAsync(string serial, CancellationToken ct = default) =>
        (await SuAsync(serial, "id -u", ct))?.Trim() == "0";

    public async Task<Dictionary<string, string>> PropsAsync(string serial, CancellationToken ct = default)
    {
        var text = await ShellAsync(serial, "getprop", ct);
        var d = new Dictionary<string, string>();
        foreach (var line in text.Split('\n'))
        {
            var i = line.IndexOf("]: [", StringComparison.Ordinal);
            if (line.Length < 6 || line[0] != '[' || i < 0) continue;
            d[line[1..i]] = line[(i + 4)..].TrimEnd(']');
        }
        return d;
    }

    public async Task PushAsync(string serial, string local, string remote, CancellationToken ct = default)
    {
        var r = await run.RunAsync(exe, ["-s", serial, "push", local, remote], TimeSpan.FromHours(1), ct: ct);
        if (!r.Ok) throw new IOException($"adb push {Path.GetFileName(local)} failed: {r.Output.Trim()}");
    }

    /// <summary>Single-quotes a command for the phone's sh, which the adb server joins with spaces.</summary>
    internal static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";
}

/// <summary>Bootloader mode. Nubia's fastboot is read-only (getvar works, flashing does not).</summary>
public sealed class Fastboot(string exe, ProcessRunner run)
{
    public async Task<List<string>> DevicesAsync(CancellationToken ct = default)
    {
        var r = await run.RunAsync(exe, ["devices"], TimeSpan.FromSeconds(15), ct: ct, quiet: true);
        return r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\t', ' ')[0].Trim()).Where(s => s.Length > 0).ToList();
    }

    public async Task<string?> GetVarAsync(string serial, string name, CancellationToken ct = default)
    {
        var r = await run.RunAsync(exe, ["-s", serial, "getvar", name], TimeSpan.FromSeconds(15), ct: ct, quiet: true);
        foreach (var line in r.Output.Split('\n'))
            if (line.StartsWith(name + ":", StringComparison.Ordinal)) return line[(name.Length + 1)..].Trim();
        return null;
    }
}
