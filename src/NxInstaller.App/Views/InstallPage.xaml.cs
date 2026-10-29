using System.Diagnostics;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NxInstaller.App.Services;
using NxInstaller.Core;
using NxInstaller.Core.Device;
using NxInstaller.Core.Install;
using NxInstaller.Core.Linux;
using NxInstaller.Core.Safety;

namespace NxInstaller.App.Views;

public sealed partial class InstallPage : Page, IWizardPage
{
    enum Phase { Ready, Running, Succeeded, Failed }

    readonly AppServices _app = AppServices.Current;
    readonly StringBuilder _log = new();
    Phase _phase = Phase.Ready;
    CancellationTokenSource? _cts;
    double _progress;

    public InstallPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { _app.Log.Line += Log_Line; Show(); };
        Unloaded += (_, _) => _app.Log.Line -= Log_Line;
    }

    public string PrimaryText => _phase switch
    {
        Phase.Ready => _app.DryRun ? "Start test run" : "Install",
        Phase.Running => "Installing…",
        Phase.Failed => "Try again",
        _ => "Close",
    };
    public bool CanContinue => _phase != Phase.Running;
    public bool CanGoBack => _phase is Phase.Ready or Phase.Failed;
    public event EventHandler? StateChanged;

    string OsName => _app.Selected?.Package?.Manifest.Name ?? "the OS";

    public async Task<bool> OnNextAsync()
    {
        if (_phase == Phase.Succeeded) { App.Window?.Close(); return false; }
        if (!_app.DryRun && !await ConfirmAsync()) return false;
        await RunAsync();
        return false; // this page stays; it is the last step
    }

    async Task<bool> ConfirmAsync()
    {
        var erase = _app.WipeRequired || _app.EraseAnyway;
        var d = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Install {OsName} now?",
            Content = _app.IsLinux
                ? "The Linux files are copied to the phone, then it restarts into EDL mode for the Linux kernel and restarts twice more on its own. "
                  + "Android's apps and data stay, but while this kernel is installed Android runs without root (KernelSU), so root-based modules stop working."
                : erase
                ? "The phone restarts into EDL mode and everything on it is erased. This can't be undone."
                : "The phone restarts into EDL mode and the new system is written. Your apps and data stay.",
            PrimaryButtonText = erase ? "Erase and install" : "Install",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await d.ShowAsync() == ContentDialogResult.Primary;
    }

    async Task RunAsync()
    {
        var entry = _app.Selected?.Package;
        if (entry is null || _app.EdlTools is null || _app.Adb is null) return;

        _phase = Phase.Running;
        _app.Busy = true;
        _cts = new CancellationTokenSource();
        Show();

        var o = new InstallOptions
        {
            Options = _app.IncludeTwrp ? ["twrp"] : [],
            Wipe = _app.WipeRequired || _app.EraseAnyway,
            BackupFirst = _app.BackupFirst,
            DryRun = _app.DryRun,
        };
        var serial = _app.Phone.Mode == PhoneMode.Android ? _app.Phone.Serial : null;
        var installer = new Installer(_app.Log, _app.Run, _app.Adb, _app.EdlTools);
        installer.Step += st => DispatcherQueue.TryEnqueue(() => SetStep(st.Title, st.Progress));
        var linux = new LinuxInstaller(_app.Log, _app.Run, _app.Adb, _app.EdlTools);
        linux.Step += st => DispatcherQueue.TryEnqueue(() => SetStep(st.Title, st.Progress));
        var lo = _app.IsLinux ? new LinuxOptions
        {
            // a test run needs no password; it uploads nothing
            PasswordHash = _app.LinuxPasswordHash ?? Sha512Crypt.Hash(Guid.NewGuid().ToString()),
            ImageBytes = (long)_app.LinuxDiskGb << 30,
            ReplaceExisting = _app.ReplaceLinux,
            BootLinux = _app.BootLinux,
            BackupFirst = _app.BackupFirst,
            DryRun = _app.DryRun,
        } : null;
        var family = _app.CurrentFamily;

        try
        {
            if (lo is not null)
            {
                if (serial is null) throw new InvalidOperationException("Start the phone into Android first: the Linux files are copied over USB debugging.");
                await Task.Run(() => linux.InstallAsync(entry, lo, serial, family, _cts.Token));
            }
            else await Task.Run(() => installer.InstallAsync(entry, o, serial, _cts.Token));
            _phase = Phase.Succeeded;
        }
        catch (OperationCanceledException)
        {
            _phase = Phase.Failed;
            Fail("Cancelled", "Nothing was written to the phone.");
        }
        catch (PolicyViolationException e)
        {
            _phase = Phase.Failed;
            Fail("Stopped for safety", e.Message);
        }
        catch (Exception e)
        {
            _phase = Phase.Failed;
            _app.Log.Error(e.ToString());
            Fail("The install didn't finish", e.Message);
        }
        finally
        {
            _app.Busy = false;
            _cts.Dispose();
            _cts = null;
        }
        Show();
    }

    void SetStep(string title, double p)
    {
        _progress = p;
        StepText.Text = title;
        Bar.Value = p;
        Percent.Text = $"{p:P0}";
        // Downloads can be cancelled; once the phone is in EDL the install must run to the end.
        CancelButton.Visibility = p < 0.42 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    void Fail(string title, string message)
    {
        Result.Severity = InfoBarSeverity.Error;
        Result.Title = title;
        Result.Message = message;
        Result.IsOpen = true;
        LogExpander.IsExpanded = true;
    }

    void Show()
    {
        var dry = _app.DryRun;
        ProgressArea.Visibility = _phase == Phase.Running ? Visibility.Visible : Visibility.Collapsed;
        if (_phase != Phase.Failed) Result.IsOpen = false;
        switch (_phase)
        {
            case Phase.Ready:
                HeroGlyph.Glyph = "\uE896";
                Heading.Text = dry ? $"Test run: {OsName}" : $"Install {OsName}";
                Intro.Text = dry
                    ? "The phone restarts into EDL mode, the lock state and partition tables are read, and every write is planned and logged, but nothing is written. Then it restarts normally."
                    : "The images are downloaded and checked first. Then the phone restarts into EDL mode, where the bootloader's lock state is checked again before anything is written.";
                break;
            case Phase.Running:
                HeroGlyph.Glyph = "\uE896";
                Heading.Text = dry ? "Test run in progress" : $"Installing {OsName}";
                Intro.Text = "This takes a while. You can watch each step below.";
                SetStep(StepText.Text.Length > 0 ? StepText.Text : "Starting", _progress);
                break;
            case Phase.Succeeded:
                HeroGlyph.Glyph = "\uE73E";
                Heading.Text = dry ? "Test run finished" : $"{OsName} is installed";
                Intro.Text = dry
                    ? "Every check passed and nothing was written. Turn off Test run on the previous page to install for real."
                    : _app.IsLinux
                    ? (_app.BootLinux
                        ? "The phone is starting Linux. The desktop comes up in about a minute; the first start also grows the Linux disk."
                        : "Linux is installed. Android keeps starting until Linux is chosen as the boot mode.")
                    : "The phone is restarting. The first start takes a few minutes; it may restart once on its own.";
                Result.Severity = InfoBarSeverity.Success;
                Result.Title = dry ? "Nothing was written" : "Done";
                Result.Message = dry ? "The planned writes are in the details below." : "You can unplug the phone once it shows the setup screen.";
                Result.IsOpen = true;
                break;
            case Phase.Failed:
                HeroGlyph.Glyph = "\uE7BA";
                Heading.Text = "Something went wrong";
                Intro.Text = "The details below say what happened. If the phone doesn't start, put it in EDL mode and run the install again.";
                break;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    void Log_Line(LogLevel level, string message)
    {
        if (level == LogLevel.Debug) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            _log.AppendLine(level >= LogLevel.Warn ? $"{level}: {message}" : message);
            LogText.Text = _log.ToString();
            LogScroll.ChangeView(null, LogScroll.ScrollableHeight + 100, null, true);
        });
    }

    void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (_app.Log.FilePath is { } p && File.Exists(p))
            Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
    }
}
