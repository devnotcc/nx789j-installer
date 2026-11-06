using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using NxInstaller.Core;
using NxInstaller.Core.Device;
using NxInstaller.Core.Edl;
using NxInstaller.Core.Packages;

namespace NxInstaller.App.Services;

/// <summary>
/// The engine objects and the wizard's state, shared by every page. Also watches the USB port so
/// the phone card in the sidebar stays current; watching pauses while an install or backup runs,
/// because adb must not touch the phone in the middle of an EDL session.
/// </summary>
public sealed partial class AppServices : ObservableObject
{
    public static AppServices Current { get; } = new();

    public Log Log { get; } = new();
    public ProcessRunner Run { get; }
    public HttpClient Http { get; } = Net.CreateClient();
    public AppSettings Settings { get; } = AppSettings.Load();

    [ObservableProperty] public partial PhoneSnapshot Phone { get; set; } = new() { Mode = PhoneMode.None };
    [ObservableProperty] public partial bool ToolsReady { get; set; }
    [ObservableProperty] public partial EdlTools? EdlTools { get; set; }
    [ObservableProperty] public partial bool Busy { get; set; }

    // Wizard state
    public List<CatalogItem> Catalog { get; } = Services.Catalog.Create();
    [ObservableProperty] public partial CatalogItem? Selected { get; set; }
    [ObservableProperty] public partial bool IncludeTwrp { get; set; } = true;
    [ObservableProperty] public partial bool WipeConfirmed { get; set; }
    [ObservableProperty] public partial bool BackupFirst { get; set; } = true;
    [ObservableProperty] public partial bool EraseAnyway { get; set; }
    /// <summary>The user says the phone (in EDL or fastboot, where its system can't be seen) already runs the selected system.</summary>
    [ObservableProperty] public partial bool AlreadyRunsIt { get; set; }
    // a claim about one system never carries over to another
    partial void OnSelectedChanged(CatalogItem? value) => AlreadyRunsIt = false;
    /// <summary>Go through every step, including EDL, but write nothing to the phone.</summary>
    [ObservableProperty] public partial bool DryRun { get; set; }

    // Linux packages
    /// <summary>SHA-512 crypt hash of the chosen Linux password; the password itself is not kept.</summary>
    public string? LinuxPasswordHash { get; set; }
    [ObservableProperty] public partial double LinuxDiskGb { get; set; } = 32;
    [ObservableProperty] public partial bool ReplaceLinux { get; set; }
    [ObservableProperty] public partial bool BootLinux { get; set; } = true;

    public bool IsLinux => Selected?.Package?.Manifest.Kind == PackageKind.Linux;

    /// <summary>Package family of the OS the phone runs now, for deciding whether data has to be erased.</summary>
    public string? CurrentFamily => Phone.Os switch
    {
        PhoneOs.EvolutionX => "evolutionx",
        PhoneOs.LineageOS => "lineageos",
        PhoneOs.Debian => "debian",
        _ => null,
    };

    /// <summary>True when the selected package must format data (different OS or first install).</summary>
    public bool WipeRequired => Selected?.Package is { } p && p.Manifest.Kind == PackageKind.Android
        && Core.Install.InstallPlanner.NeedsWipe(p.Manifest,
            CurrentFamily ?? (OsUnseen && AlreadyRunsIt ? p.Manifest.Wipe.UnlessFamily.FirstOrDefault() : null));

    /// <summary>The phone is connected in a mode where its system can't be seen (EDL, fastboot).</summary>
    public bool OsUnseen => Phone.Mode is PhoneMode.Edl or PhoneMode.Fastboot;

    /// <summary>The user can tell the app the phone already runs the selected system (so data can be kept).</summary>
    public bool CanClaimAlreadyRuns => OsUnseen && Selected?.Package is { } p && p.Manifest.Kind == PackageKind.Android
        && !p.Manifest.Wipe.Always && p.Manifest.Wipe.UnlessFamily.Count > 0;

    public Adb? Adb { get; private set; }
    public Fastboot? Fastboot { get; private set; }

    DispatcherQueueTimer? _timer;
    bool _probing;

    AppServices()
    {
        Run = new ProcessRunner(Log);
        EdlTools = Core.Edl.EdlTools.FindDefault(Settings.EdlToolsFolder);
    }

    /// <summary>Downloads adb/fastboot if needed and starts watching for the phone.</summary>
    public async Task StartAsync(DispatcherQueue ui)
    {
        try
        {
            await PlatformTools.EnsureAsync(Http, Log);
            Adb = new Adb(PlatformTools.AdbExe, Run);
            Fastboot = new Fastboot(PlatformTools.FastbootExe, Run);
            ToolsReady = true;
        }
        catch (Exception e)
        {
            Log.Error($"could not get adb/fastboot: {e.Message}");
            return;
        }
        _timer = ui.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(3);
        _timer.Tick += async (_, _) => await ProbeAsync();
        _timer.Start();
        await ProbeAsync();
    }

    public async Task ProbeAsync()
    {
        if (_probing || Busy || Adb is null || Fastboot is null) return;
        _probing = true;
        try { Phone = await new DeviceProbe(Adb, Fastboot).ProbeAsync(); }
        catch (Exception e) { Log.Debug($"probe: {e.Message}"); }
        finally { _probing = false; }
    }

    public bool SetEdlToolsFolder(string folder)
    {
        var t = Core.Edl.EdlTools.Find(folder);
        if (t is null) return false;
        EdlTools = t;
        Settings.EdlToolsFolder = folder;
        Settings.Save();
        return true;
    }

    public IPackageSource Source => Settings.LocalPackages is { Length: > 0 } dir
        ? new FolderSource(dir)
        : new GitHubReleaseSource(Http, Settings.Repository);

    /// <summary>Matches the catalog against the packages in the current source.</summary>
    public async Task<string?> RefreshPackagesAsync(CancellationToken ct = default)
    {
        List<PackageEntry> found;
        try { found = await Source.ListAsync(ct); }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            Log.Warn($"could not list packages: {e.Message}");
            foreach (var c in Catalog) c.Package = null;
            return $"Could not reach {Source.Name}.";
        }
        foreach (var c in Catalog)
            c.Package = found.Newest(c.Id);
        return null;
    }
}
