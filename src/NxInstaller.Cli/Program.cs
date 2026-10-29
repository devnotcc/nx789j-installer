using NxInstaller.Core;
using NxInstaller.Core.Backup;
using NxInstaller.Core.Device;
using NxInstaller.Core.Edl;
using NxInstaller.Core.Install;
using NxInstaller.Core.Linux;
using NxInstaller.Core.Packages;
using NxInstaller.Core.Pif;
using NxInstaller.Core.Safety;

// Headless front-end to the engine: the same actions the GUI runs, for testing and scripting.
// Writes to the phone need an explicit command and print exactly what they will do first.

var ct = Cancellation();
using var log = new Log();
log.Line += (lvl, m) => Console.WriteLine(lvl >= LogLevel.Warn ? $"[{lvl}] {m}" : m);
var run = new ProcessRunner(log);

if (args.Length == 0) { Usage(); return 1; }

try
{
    switch (args[0])
    {
        case "probe": return await Probe();
        case "checks": return await Checks(args.ElementAtOrDefault(1));
        case "keybox": return await CheckKeybox(args.ElementAtOrDefault(1));
        case "pif-status": return await PifStatus();
        case "pif-swap": return await PifSwap(args.ElementAtOrDefault(1));
        case "list": return await ListPackages(args.ElementAtOrDefault(1));
        case "edl-info": return await EdlInfo();
        case "backup": return await BackupPartitions(args.ElementAtOrDefault(1), args.Contains("--super"), args.Contains("--userdata"));
        case "install": return await InstallPkg(args);
        case "repair-slot": return await RepairSlot(args.Contains("--dry-run"));
        case "kernel": return await Kernel(args);
        case "pack": return await Pack(args.ElementAtOrDefault(1), args.ElementAtOrDefault(2));
        default: Usage(); return 1;
    }
}
catch (OperationCanceledException) { log.Warn("cancelled"); return 130; }
catch (Exception e) { log.Error(e.Message); return 1; }

// ---- commands -------------------------------------------------------------

async Task<Adb> AdbAsync()
{
    using var http = Net.CreateClient();
    await PlatformTools.EnsureAsync(http, log, null, ct);
    return new Adb(PlatformTools.AdbExe, run);
}

async Task<int> Probe()
{
    var adb = await AdbAsync();
    var probe = new DeviceProbe(adb, new Fastboot(PlatformTools.FastbootExe, run));
    var s = await probe.ProbeAsync(ct);
    Console.WriteLine($"mode     {s.Mode}");
    if (s.Serial is not null) Console.WriteLine($"serial   {s.Serial}");
    if (s.Model is not null) Console.WriteLine($"model    {s.Model}");
    if (s.Os != PhoneOs.Unknown) Console.WriteLine($"os       {s.Os} {s.OsVersion}");
    if (s.Slot is not null) Console.WriteLine($"slot     {s.Slot}");
    if (s.Battery is not null) Console.WriteLine($"battery  {s.Battery}%");
    if (s.Kernel is not null) Console.WriteLine($"kernel   {s.Kernel}");
    if (s.Baseband is not null) Console.WriteLine($"baseband {s.Baseband}");
    if (s.Mode == PhoneMode.Android) Console.WriteLine($"root     {s.Root}");
    if (s.EdlPort is not null) Console.WriteLine($"edl port COM{s.EdlPort}");
    return 0;
}

async Task<int> Checks(string? _)
{
    var adb = await AdbAsync();
    var probe = new DeviceProbe(adb, new Fastboot(PlatformTools.FastbootExe, run));
    var s = await probe.ProbeAsync(ct);
    var checks = DeviceGate.CheckPhone(s, null);
    foreach (var c in checks) Console.WriteLine($"  {Mark(c.State)} {c.Name}: {c.Detail}");
    Console.WriteLine(checks.Blocks() ? "\nBLOCKED" : "\nOK to continue");
    return checks.Blocks() ? 2 : 0;
}

async Task<int> CheckKeybox(string? file)
{
    if (file is null) { log.Error("usage: nxcli keybox <keybox.xml>"); return 1; }
    RevocationList? crl = null;
    try { using var http = Net.CreateClient(); crl = await RevocationList.FetchAsync(http, ct); log.Info($"revocation list: {crl.FetchedAt:u}"); }
    catch (Exception e) { log.Warn($"could not fetch the revocation list ({e.Message}); checking offline"); }

    var rep = Keybox.Check(await File.ReadAllTextAsync(file, ct), crl, DateTimeOffset.UtcNow);
    Console.WriteLine($"device id: {rep.DeviceId}");
    foreach (var f in rep.Findings) Console.WriteLine($"  - {f}");
    Console.WriteLine($"\nverdict: {rep.Verdict}  ({(rep.Usable ? "safe to install" : "do NOT install")})");
    return rep.Usable ? 0 : 2;
}

async Task<int> PifStatus()
{
    var adb = await AdbAsync();
    var s = (await new DeviceProbe(adb, new Fastboot(PlatformTools.FastbootExe, run)).ProbeAsync(ct));
    if (s.Serial is null || s.Mode != PhoneMode.Android) { log.Error("phone is not booted to Android"); return 1; }
    var tb = new PifToolbox(adb, log);
    var st = await tb.ReadStatusAsync(s.Serial, ct);
    if (!st.RootAvailable) { log.Error("root is not available"); return 1; }
    Console.WriteLine($"modules:     {string.Join(", ", st.Modules)}");
    Console.WriteLine($"trickystore: {(st.TrickyStoreRunning ? "running" : "NOT running")}");
    Console.WriteLine($"zygisk:      {(st.ZygiskRunning ? "running" : "NOT running")}");
    Console.WriteLine($"keybox:      {st.KeyboxDeviceId ?? "(none)"}  leaf {st.KeyboxLeafSerial ?? "?"}");
    Console.WriteLine($"targets:     {string.Join(", ", st.Targets)}");
    if (st.EvoxSpoofStacked) Console.WriteLine("WARNING: EvoX built-in spoof is set and will stack on TrickyStore (breaks strong).");
    if (st.KeyboxLeafSerial is { } leaf)
    {
        try { using var http = Net.CreateClient(); var crl = await RevocationList.FetchAsync(http, ct);
              Console.WriteLine($"current keybox on CRL: {crl.StatusOf(leaf) ?? "not listed (good)"}"); }
        catch { }
    }
    return 0;
}

async Task<int> PifSwap(string? file)
{
    if (file is null) { log.Error("usage: nxcli pif-swap <keybox.xml>"); return 1; }
    using var http = Net.CreateClient();
    var crl = await RevocationList.FetchAsync(http, ct);
    var rep = Keybox.Check(await File.ReadAllTextAsync(file, ct), crl, DateTimeOffset.UtcNow);
    foreach (var f in rep.Findings) Console.WriteLine($"  - {f}");
    if (!rep.Usable) { log.Error($"keybox is {rep.Verdict}; not installing"); return 2; }
    var adb = await AdbAsync();
    var s = await new DeviceProbe(adb, new Fastboot(PlatformTools.FastbootExe, run)).ProbeAsync(ct);
    if (s.Serial is null) { log.Error("no phone"); return 1; }
    await new PifToolbox(adb, log).SwapKeyboxAsync(s.Serial, file, rep, ct);
    Console.WriteLine("keybox installed; re-run the Play integrity check on the phone.");
    return 0;
}

async Task<int> ListPackages(string? source)
{
    var packages = await LoadSource(source);
    foreach (var e in packages)
    {
        var m = e.Manifest;
        Console.WriteLine($"{(m.Enabled ? "  " : "x ")}{m.Id,-22} {m.Version,-14} {m.Kind,-8} {m.Name}{(m.Enabled ? "" : $"  [{m.DisabledReason}]")}");
    }
    return 0;
}

async Task<int> EdlInfo()
{
    var (edl, _) = await ConnectEdl();
    Console.WriteLine($"bootloader: {(edl.Unlock!.Unlocked ? "UNLOCKED" : "LOCKED")}");
    Console.WriteLine($"partitions: {edl.Table!.All.Count}");
    try { Console.WriteLine($"active slot: {InstallPlanner.ActiveSlot(edl.Table)}"); } catch (Exception e) { Console.WriteLine($"active slot: {e.Message}"); }
    await edl.ResetAsync(ct);
    return 0;
}

async Task<int> BackupPartitions(string? dest, bool super, bool userdata)
{
    dest ??= Path.Combine(AppPaths.Root, "backups", $"full-{DateTime.Now:yyyyMMdd-HHmmss}");
    var (edl, _) = await ConnectEdl();
    var bk = new EdlBackup(edl, log) { IncludeSuper = super, IncludeUserdata = userdata };
    bk.Progress += (name, p) => Console.Write($"\r  {p,6:P0}  {name,-24}");
    await bk.RunAsync(dest, ct);
    Console.WriteLine($"\nsaved to {dest}");
    await edl.ResetAsync(ct);
    return 0;
}

async Task<int> InstallPkg(string[] a)
{
    var id = a.ElementAtOrDefault(1);
    if (id is null) { log.Error("usage: nxcli install <package-id> [--source DIR] [--dry-run] [--twrp] [--no-backup]"); return 1; }
    var source = Opt(a, "--source");
    var entry = (await LoadSource(source)).FirstOrDefault(e => e.Manifest.Id == id)
        ?? throw new InvalidOperationException($"package {id} not found");

    var adb = await AdbAsync();
    var probe = await new DeviceProbe(adb, new Fastboot(PlatformTools.FastbootExe, run)).ProbeAsync(ct);
    var tools = EdlTools.FindDefault(null) ?? throw new InvalidOperationException("EDL tools (ZTE toolbox) not found; pass its folder in settings");
    if (entry.Manifest.Kind == PackageKind.Linux) return await InstallLinux(a, entry, adb, probe, tools);

    var o = new InstallOptions
    {
        DryRun = a.Contains("--dry-run"),
        BackupFirst = !a.Contains("--no-backup"),
        Options = a.Contains("--twrp") ? ["twrp"] : [],
        Wipe = a.Contains("--wipe") || InstallPlanner.NeedsWipe(entry.Manifest, FamilyOf(probe.Os)),
    };
    Console.WriteLine($"install {entry.Manifest.Name} {entry.Manifest.Version}  (wipe={o.Wipe}, dryrun={o.DryRun})");

    var inst = new Installer(log, run, adb, tools);
    inst.Step += st => Console.WriteLine($"  [{st.Progress,5:P0}] {st.Title}");
    await inst.InstallAsync(entry, o, probe.Mode == PhoneMode.Android ? probe.Serial : null, ct);
    return 0;
}

async Task<int> InstallLinux(string[] a, PackageEntry entry, Adb adb, PhoneSnapshot probe, EdlTools tools)
{
    if (probe.Mode != PhoneMode.Android || probe.Serial is null) { log.Error("start the phone into Android first (the Linux files are copied over adb)"); return 1; }
    if (LinuxInstaller.CheckBase(entry.Manifest, FamilyOf(probe.Os)) is { } why) { log.Error(why); return 1; }
    var dry = a.Contains("--dry-run");
    string password;
    if (Opt(a, "--password-file") is { } pf) password = File.ReadAllText(pf).TrimEnd('\r', '\n');
    else
    {
        password = ReadSecret("Password for the Linux account 'nx': ");
        if (ReadSecret("Again: ") != password) { log.Error("the passwords differ"); return 1; }
    }
    if (password.Length < 4) { log.Error("use a password of at least 4 characters"); return 1; }
    var gb = Opt(a, "--size") is { } sz ? long.Parse(sz) : 32;
    var o = new LinuxOptions
    {
        PasswordHash = Sha512Crypt.Hash(password),
        ImageBytes = gb << 30,
        ReplaceExisting = a.Contains("--replace"),
        BootLinux = !a.Contains("--stay-android"),
        BackupFirst = !a.Contains("--no-backup"),
        DryRun = dry,
    };
    Console.WriteLine($"install {entry.Manifest.Name} {entry.Manifest.Version} on top of {probe.Os}  (disk {gb} GiB, replace={o.ReplaceExisting}, dryrun={dry})");
    var inst = new LinuxInstaller(log, run, adb, tools);
    inst.Step += st => Console.WriteLine($"  [{st.Progress,5:P0}] {st.Title}");
    await inst.InstallAsync(entry, o, probe.Serial, FamilyOf(probe.Os), ct);
    return 0;
}

static string ReadSecret(string prompt)
{
    Console.Write(prompt);
    var sb = new System.Text.StringBuilder();
    while (true)
    {
        var k = Console.ReadKey(intercept: true);
        if (k.Key == ConsoleKey.Enter) break;
        if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
        if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
    }
    Console.WriteLine();
    return sb.ToString();
}

async Task<int> RepairSlot(bool dry)
{
    var tools = EdlTools.FindDefault(null) ?? throw new InvalidOperationException("EDL tools not found (ZTE toolbox)");
    var edl = new EdlSession(tools, run, log, Path.Combine(AppPaths.Work, "edl")) { DryRun = dry };
    // a phone with an unbootable slot can't reach Android; it has to be put in EDL by hand
    var port = EdlSession.FindPort() ?? await edl.WaitForPortAsync(TimeSpan.FromMinutes(3), ct);
    await edl.ConnectAsync(port, ct);
    var slot = InstallPlanner.ActiveSlot(edl.Table!);
    var changed = await edl.RepairSlotAsync(slot, ct);
    Console.WriteLine(changed ? $"slot {slot} repaired" : $"slot {slot} was not marked unbootable; nothing written");
    await edl.ResetAsync(ct);
    return 0;
}

async Task<int> Kernel(string[] a)
{
    var id = a.ElementAtOrDefault(1);
    if (id is null) { log.Error("usage: nxcli kernel <package-id> [--source DIR] [--dry-run]"); return 1; }
    var entry = (await LoadSource(Opt(a, "--source"))).FirstOrDefault(e => e.Manifest.Id == id)
        ?? throw new InvalidOperationException($"package {id} not found");
    var adb = await AdbAsync();
    var probe = await new DeviceProbe(adb, new Fastboot(PlatformTools.FastbootExe, run)).ProbeAsync(ct);
    var tools = EdlTools.FindDefault(null) ?? throw new InvalidOperationException("EDL tools not found (ZTE toolbox)");
    var inst = new Installer(log, run, adb, tools);
    inst.Step += st => Console.WriteLine($"  [{st.Progress,5:P0}] {st.Title}");
    await inst.InstallAsync(entry, Installer.KernelOnly(a.Contains("--dry-run")), probe.Mode == PhoneMode.Android ? probe.Serial : null, ct);
    return 0;
}

async Task<int> Pack(string? spec, string? outDir)
{
    if (spec is null || outDir is null) { log.Error("usage: nxcli pack <spec.json> <out-dir>"); return 1; }
    var m = await PackageBuilder.BuildAsync(PackageBuilder.LoadSpec(spec), outDir, log, ct: ct);
    Console.WriteLine($"built {m.Id} {m.Version}: {m.Images.Count} image(s), {m.Files.Count} file(s), {m.Images.Sum(i => i.Parts.Count) + m.Files.Sum(f => f.Parts.Count)} part(s) in {outDir}");
    return 0;
}

// ---- helpers --------------------------------------------------------------

async Task<List<PackageEntry>> LoadSource(string? source)
{
    IPackageSource src = source is not null ? new FolderSource(source)
        : new FolderSource(Path.Combine(AppPaths.Root, "packages"));
    return await src.ListAsync(ct);
}

async Task<(EdlSession, EdlTools)> ConnectEdl()
{
    var tools = EdlTools.FindDefault(null) ?? throw new InvalidOperationException("EDL tools not found (ZTE toolbox)");
    if (!tools.FirehoseKnown) log.Warn("the firehose programmer is not a version this app was tested with");
    var edl = new EdlSession(tools, run, log, Path.Combine(AppPaths.Work, "edl"));
    var port = EdlSession.FindPort();
    if (port is null)
    {
        var adb = await AdbAsync();
        var s = await new DeviceProbe(adb, new Fastboot(PlatformTools.FastbootExe, run)).ProbeAsync(ct);
        if (s.Serial is null || s.Mode != PhoneMode.Android) throw new InvalidOperationException("boot to Android or put the phone in EDL mode first");
        log.Info("rebooting to EDL");
        await adb.RunAsync(s.Serial, ["reboot", "edl"], TimeSpan.FromSeconds(30), ct);
        port = await edl.WaitForPortAsync(TimeSpan.FromMinutes(2), ct);
    }
    await edl.ConnectAsync(port, ct);
    return (edl, tools);
}

static string? FamilyOf(PhoneOs os) => os switch
{
    PhoneOs.EvolutionX => "evolutionx", PhoneOs.LineageOS => "lineageos", PhoneOs.Debian => "debian", _ => null,
};

static string? Opt(string[] a, string name)
{
    var i = Array.IndexOf(a, name);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}

static string Mark(CheckState s) => s switch { CheckState.Pass => "[+]", CheckState.Warn => "[!]", _ => "[x]" };

static CancellationToken Cancellation()
{
    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    return cts.Token;
}

static void Usage() => Console.WriteLine("""
    nxcli - NX789J installer engine (command line)

      probe                      show the connected phone
      checks                     run the pre-install checks
      keybox <file>              check a keybox.xml against Google's revocation list
      pif-status                 show the Play Integrity stack on the phone
      pif-swap <file>            check then install a keybox (no reboot)
      list [--source DIR]        list available packages
      edl-info                   reboot to EDL and show lock state + partitions (read only)
      backup [DIR] [--super] [--userdata]   full partition backup over EDL (read only)
      install <id> [--source DIR] [--dry-run] [--twrp] [--wipe] [--no-backup]
                 Linux packages: [--size GB] [--replace] [--stay-android] [--password-file FILE]
      repair-slot [--dry-run]    clear the 'unbootable' mark on the active slot (phone in EDL)
      kernel <id> [--dry-run]    rewrite only boot + init_boot from a package (keeps data)
      pack <spec.json> <out-dir>   build a package (zstd, split parts, manifest.json)
    """);
