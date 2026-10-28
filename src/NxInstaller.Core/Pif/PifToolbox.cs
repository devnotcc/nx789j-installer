using NxInstaller.Core.Device;

namespace NxInstaller.Core.Pif;

public sealed record PifStatus(
    bool RootAvailable,
    IReadOnlyList<string> Modules,
    bool TrickyStoreRunning,
    bool ZygiskRunning,
    string? KeyboxDeviceId,
    string? KeyboxLeafSerial,
    IReadOnlyList<string> Targets,
    bool EvoxSpoofStacked);

/// <summary>
/// Reads and repairs the Play Integrity stack on the phone over adb (root), the same pieces the
/// working setup uses: Zygisk Next + PlayIntegrityFix + TrickyStoreOSS + SUSFS, with the keybox at
/// /data/adb/tricky_store/keybox.xml. The one write the toolbox makes to the phone is swapping in a
/// keybox the user chose and that <see cref="Keybox"/> has already cleared; everything else is read.
///
/// EvoX's built-in spoof must stay cleared, or it stacks on top of TrickyStore and breaks the strong
/// verdict — the toolbox reports that and can clear it.
/// </summary>
public sealed class PifToolbox(Adb adb, Log log)
{
    const string TsDir = "/data/adb/tricky_store";
    const string KeyboxPath = TsDir + "/keybox.xml";

    public async Task<PifStatus> ReadStatusAsync(string serial, CancellationToken ct)
    {
        if (!await adb.HasRootAsync(serial, ct))
            return new PifStatus(false, [], false, false, null, null, [], false);

        var modules = (await adb.SuAsync(serial,
            "for m in /data/adb/modules/*/; do [ -e \"$m/disable\" ] || grep -h '^id=' \"$m/module.prop\" 2>/dev/null | cut -d= -f2; done", ct)
            ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var procs = await adb.SuAsync(serial, "ps -A -o NAME 2>/dev/null", ct) ?? "";
        var deviceId = await adb.SuAsync(serial, $"grep -o 'DeviceID=\"[^\"]*\"' {KeyboxPath} 2>/dev/null | head -1 | cut -d'\"' -f2", ct);
        var leaf = await KeyboxLeafSerialAsync(serial, ct);
        var targets = (await adb.SuAsync(serial, $"cat {TsDir}/target.txt 2>/dev/null", ct) ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var spoof = await adb.ShellAsync(serial, "settings get secure spoof_trickystore_keybox", ct);
        var evoxStacked = !string.IsNullOrWhiteSpace(spoof) && spoof.Trim() != "null";

        return new PifStatus(
            true, modules,
            procs.Contains("TrickyStore", StringComparison.OrdinalIgnoreCase),
            procs.Contains("zygisk", StringComparison.OrdinalIgnoreCase),
            string.IsNullOrWhiteSpace(deviceId) ? null : deviceId.Trim(),
            leaf, targets, evoxStacked);
    }

    /// <summary>The leaf certificate serial currently installed, so the UI can show it against the CRL.</summary>
    public async Task<string?> KeyboxLeafSerialAsync(string serial, CancellationToken ct)
    {
        // openssl isn't on the phone; pull the file and read it on the PC.
        var tmp = Path.Combine(AppPaths.Work, "pif");
        Directory.CreateDirectory(tmp);
        var local = Path.Combine(tmp, "current-keybox.xml");
        var pulled = await adb.SuAsync(serial, $"cat {KeyboxPath} 2>/dev/null", ct);
        if (string.IsNullOrWhiteSpace(pulled)) return null;
        await File.WriteAllTextAsync(local, pulled, ct);
        try
        {
            var rep = Keybox.Check(pulled, null, DateTimeOffset.UtcNow);
            return rep.Keys.FirstOrDefault()?.LeafSerial;
        }
        catch { return null; }
    }

    /// <summary>
    /// Installs a keybox the user picked, after it has been checked. Backs up the current one, copies
    /// the new file in, and restarts the integrity clients so the change takes effect without a reboot
    /// (what restore.sh does). The caller must pass a report with <see cref="KeyboxReport.Usable"/> true.
    /// </summary>
    public async Task SwapKeyboxAsync(string serial, string localKeyboxFile, KeyboxReport report, CancellationToken ct)
    {
        if (!report.Usable)
            throw new InvalidOperationException($"Refusing to install this keybox: {report.Verdict}. Fix the findings first.");
        if (!await adb.HasRootAsync(serial, ct))
            throw new InvalidOperationException("Root is not available on the phone, so the keybox cannot be swapped.");

        var remoteTmp = "/data/local/tmp/nx-keybox.xml";
        await adb.PushAsync(serial, localKeyboxFile, remoteTmp, ct);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        // Back up the old keybox, move the new one in, fix perms, then restart the clients.
        // The gms.unstable process is killed by PID (never a name match) and vending is stopped
        // so Play re-reads integrity; TrickyStore reloads the keybox file on its own.
        var script =
            $"set -e; mkdir -p {TsDir}; " +
            $"[ -f {KeyboxPath} ] && cp {KeyboxPath} {TsDir}/keybox.bak-{stamp}; " +
            $"cp {remoteTmp} {KeyboxPath}; chmod 644 {KeyboxPath}; rm -f {remoteTmp}; " +
            $"for p in $(pidof com.google.android.gms.unstable 2>/dev/null); do kill -9 $p; done; " +
            "am force-stop com.android.vending; " +
            "echo swapped";
        var r = await adb.SuAsync(serial, script, ct);
        if (r?.TrimEnd().EndsWith("swapped") != true)
            throw new IOException($"Keybox swap did not complete. The old keybox backup is at {TsDir}/keybox.bak-{stamp}.");
        log.Info($"keybox installed (device id {report.DeviceId}); old one backed up as keybox.bak-{stamp}");
    }

    /// <summary>Clears EvoX's built-in spoof settings so it stops stacking on TrickyStore.</summary>
    public async Task ClearEvoxSpoofAsync(string serial, CancellationToken ct)
    {
        foreach (var key in new[] { "spoof_trickystore_keybox", "spoof_trickystore_target", "spoof_pif_config" })
            await adb.ShellAsync(serial, $"settings delete secure {key}", ct);
        log.Info("EvoX built-in spoof cleared (takes effect after a reboot)");
    }
}
