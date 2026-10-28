using System.Diagnostics;
using System.Text;

namespace NxInstaller.Core;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
    public string Output => StdErr.Length == 0 ? StdOut : StdOut + StdErr;
}

/// <summary>Runs the external tools (adb, fastboot, QSaharaServer, fh_loader) and logs what they do.</summary>
public sealed class ProcessRunner(Log log)
{
    public async Task<ProcessResult> RunAsync(string exe, IEnumerable<string> args, TimeSpan? timeout = null,
        Action<string>? onLine = null, CancellationToken ct = default, bool quiet = false)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (!quiet) log.Debug($"$ {Path.GetFileName(exe)} {string.Join(' ', psi.ArgumentList.Select(Quote))}");

        using var p = new Process { StartInfo = psi };
        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is null) return; outSb.AppendLine(e.Data); onLine?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is null) return; errSb.AppendLine(e.Data); onLine?.Invoke(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) cts.CancelAfter(t);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            log.Warn($"{Path.GetFileName(exe)} timed out after {timeout}");
            return new ProcessResult(-1, outSb.ToString(), errSb + "\n(timed out)");
        }
        p.WaitForExit(); // flush the async readers
        var r = new ProcessResult(p.ExitCode, outSb.ToString(), errSb.ToString());
        if (!quiet && !r.Ok) log.Debug($"  exit {r.ExitCode}: {Trim(r.Output)}");
        return r;
    }

    static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
    static string Trim(string s) => s.Length > 600 ? s[..600] + "..." : s.Trim();
}
