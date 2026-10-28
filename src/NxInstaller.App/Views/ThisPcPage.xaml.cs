using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using NxInstaller.App.Services;
using NxInstaller.Core;
using NxInstaller.Core.Safety;

namespace NxInstaller.App.Views;

public sealed partial class ThisPcPage : Page, IWizardPage
{
    readonly AppServices _app = AppServices.Current;
    List<Check> _checks = [];

    public ThisPcPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { _app.PropertyChanged += App_Changed; Refresh(); };
        Unloaded += (_, _) => _app.PropertyChanged -= App_Changed;
    }

    public string PrimaryText => "Next";
    public bool CanContinue => _checks.Count > 0 && !_checks.Blocks();
    public event EventHandler? StateChanged;

    void App_Changed(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppServices.ToolsReady) or nameof(AppServices.EdlTools)) DispatcherQueue.TryEnqueue(Refresh);
    }

    void Refresh()
    {
        var rows = new List<(Check, UIElement?)>
        {
            (SpaceCheck(), null),
            (_app.ToolsReady
                ? new Check("adb and fastboot", CheckState.Pass, "Google's platform-tools are ready.")
                : new Check("adb and fastboot", CheckState.Fail, "Downloading Google's platform-tools… (needs internet the first time)"), null),
            (DriverCheck(), null),
            (EdlCheck(), Button("Browse…", BrowseTools)),
            (SourceCheck(), Button(_app.Settings.LocalPackages is null ? "Use a folder…" : "Use GitHub", ToggleSource)),
        };
        _checks = rows.Select(r => r.Item1).ToList();
        CheckList.Render(Rows, rows);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    Check SpaceCheck()
    {
        var pkg = _app.Selected?.Package?.Manifest;
        // Compressed parts plus the raw images they turn into, plus room for a backup.
        long need = pkg is null ? 40L << 30 : pkg.Images.Sum(i => i.Size + i.Parts.Sum(p => p.Size)) + (2L << 30);
        var root = Path.GetPathRoot(AppPaths.Root)!;
        var free = new DriveInfo(root).AvailableFreeSpace;
        var text = $"{Gb(free)} free on {root.TrimEnd('\\')}, {Gb(need)} needed.";
        return free >= need ? new Check("Disk space", CheckState.Pass, text) : new Check("Disk space", CheckState.Fail, text + " Free up some space first.");
    }

    static Check DriverCheck()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\qcusbser");
        return key is not null
            ? new Check("Qualcomm USB driver", CheckState.Pass, "The 9008 (EDL) driver is installed.")
            : new Check("Qualcomm USB driver", CheckState.Warn,
                "The Qualcomm HS-USB QDLoader 9008 driver wasn't found. Install it from the ZTE toolbox, or the phone won't be seen in EDL mode.");
    }

    Check EdlCheck()
    {
        var t = _app.EdlTools;
        if (t is null)
            return new Check("EDL tools", CheckState.Fail, "Point the installer at the ZTE toolbox folder (the one with bin\\tool\\Win and bin\\res\\NX789J).");
        return t.FirehoseKnown
            ? new Check("EDL tools", CheckState.Pass, $"Found in {CommonRoot(t.Sahara, t.Firehose)}.")
            : new Check("EDL tools", CheckState.Warn, "Found, but this firehose loader isn't one the installer was tested with.");
    }

    Check SourceCheck() => _app.Settings.LocalPackages is { } dir
        ? new Check("Where the OS comes from", Directory.Exists(dir) ? CheckState.Pass : CheckState.Fail, $"The folder {dir}.")
        : new Check("Where the OS comes from", CheckState.Pass, $"Downloaded from GitHub ({_app.Settings.Repository}).");

    async void BrowseTools()
    {
        if (await CheckList.PickFolderAsync() is not { } dir) return;
        if (!_app.SetEdlToolsFolder(dir))
            await new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "Tools not found", CloseButtonText = "OK",
                Content = "That folder doesn't contain QSaharaServer.exe, fh_loader.exe, qcedlcmdhelper.exe and the NX789J devprg. Pick the ZTE toolbox folder.",
            }.ShowAsync();
        Refresh();
    }

    async void ToggleSource()
    {
        if (_app.Settings.LocalPackages is not null) _app.Settings.LocalPackages = null;
        else if (await CheckList.PickFolderAsync() is { } dir) _app.Settings.LocalPackages = dir;
        else return;
        _app.Settings.Save();
        await _app.RefreshPackagesAsync();
        Refresh();
    }

    /// <summary>The folder both tools sit under (the toolbox root), e.g. C:\zte\bin.</summary>
    static string CommonRoot(string a, string b)
    {
        var x = Path.GetDirectoryName(a)!;
        while (!b.StartsWith(x + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(x) is { } up) x = up;
        return x;
    }

    static Button Button(string text, Action click)
    {
        var b = new Button { Content = text };
        b.Click += (_, _) => click();
        return b;
    }

    static string Gb(long bytes) => $"{bytes / (double)(1L << 30):0.#} GB";
}
