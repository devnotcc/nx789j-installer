using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NxInstaller.App.Services;
using NxInstaller.Core.Device;
using NxInstaller.Core.Pif;
using NxInstaller.Core.Safety;

namespace NxInstaller.App.Views;

public sealed partial class PifPage : Page
{
    readonly AppServices _app = AppServices.Current;
    string? _file;
    KeyboxReport? _report;
    RevocationList? _crl;

    public PifPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshStatusAsync();
    }

    string? Serial => _app.Phone is { Mode: PhoneMode.Android, Serial: { } s } ? s : null;

    async Task<RevocationList?> CrlAsync()
    {
        if (_crl is not null && DateTimeOffset.UtcNow - _crl.FetchedAt < TimeSpan.FromMinutes(10)) return _crl;
        try { _crl = await RevocationList.FetchAsync(_app.Http, CancellationToken.None); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { _app.Log.Warn($"revocation list: {e.Message}"); }
        return _crl;
    }

    async Task RefreshStatusAsync()
    {
        StatusBusy.IsActive = true;
        RefreshButton.IsEnabled = false;
        var rows = new List<Check>();
        var stacked = false;
        if (Serial is not { } serial || _app.Adb is null)
        {
            rows.Add(new Check("Phone", CheckState.Warn, "Connect the phone, booted to Android with USB debugging on."));
        }
        else
        {
            var st = await new PifToolbox(_app.Adb, _app.Log).ReadStatusAsync(serial, CancellationToken.None);
            if (!st.RootAvailable)
                rows.Add(new Check("Root", CheckState.Fail, "Root isn't available to adb. In the KernelSU or Magisk app, grant superuser to Shell."));
            else
            {
                rows.Add(Module(st, "zygisksu", "Zygisk Next", st.ZygiskRunning));
                rows.Add(Module(st, "playintegrityfix", "Play Integrity Fix", true));
                rows.Add(Module(st, "tricky_store", "TrickyStore", st.TrickyStoreRunning));
                if (st.KeyboxLeafSerial is null)
                    rows.Add(new Check("Keybox", CheckState.Fail, "No keybox installed."));
                else
                {
                    var crl = await CrlAsync();
                    var status = crl?.StatusOf(st.KeyboxLeafSerial);
                    rows.Add(crl is null
                        ? new Check("Keybox", CheckState.Warn, $"{st.KeyboxDeviceId} (couldn't reach Google to check it)")
                        : status is null
                            ? new Check("Keybox", CheckState.Pass, $"{st.KeyboxDeviceId}: not revoked")
                            : new Check("Keybox", CheckState.Fail, $"{st.KeyboxDeviceId}: {status} by Google. This is why integrity fails; install a new one below."));
                }
                stacked = st.EvoxSpoofStacked;
                if (stacked)
                    rows.Add(new Check("Evolution X spoof", CheckState.Warn, "The ROM's built-in spoof is also active and conflicts with TrickyStore (breaks the strong verdict)."));
            }
        }
        CheckList.Render(StatusRows, rows.Select(r => (r, (UIElement?)null)));
        ClearSpoofButton.Visibility = stacked ? Visibility.Visible : Visibility.Collapsed;
        StatusBusy.IsActive = false;
        RefreshButton.IsEnabled = true;
        UpdateInstallButton();
    }

    static Check Module(PifStatus st, string id, string name, bool running) =>
        !st.Modules.Contains(id) ? new Check(name, CheckState.Fail, "Not installed or disabled.")
        : running ? new Check(name, CheckState.Pass, "Installed and running.")
        : new Check(name, CheckState.Warn, "Installed but not running. Restart the phone.");

    async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshStatusAsync();

    async void ClearSpoof_Click(object sender, RoutedEventArgs e)
    {
        if (Serial is not { } s || _app.Adb is null) return;
        await new PifToolbox(_app.Adb, _app.Log).ClearEvoxSpoofAsync(s, CancellationToken.None);
        Show(InfoBarSeverity.Informational, "Built-in spoof turned off", "Restart the phone for it to take effect.");
        await RefreshStatusAsync();
    }

    async void Choose_Click(object sender, RoutedEventArgs e)
    {
        if (await CheckList.PickFileAsync(".xml") is not { } file) return;
        _file = file;
        FileName.Text = Path.GetFileName(file);
        FileHint.Text = "Checking…";
        var crl = await CrlAsync();
        _report = Keybox.Check(await File.ReadAllTextAsync(file), crl, DateTimeOffset.UtcNow);
        FileHint.Text = _report.DeviceId.Length > 0 ? _report.DeviceId : "(no device id)";

        var verdict = _report.Verdict switch
        {
            KeyboxVerdict.Good => new Check("Result", CheckState.Pass, "Not revoked and valid. Safe to install."),
            KeyboxVerdict.ExpiringSoon => new Check("Result", CheckState.Warn, "Usable, but it expires soon."),
            KeyboxVerdict.Revoked => new Check("Result", CheckState.Fail, "Revoked by Google. Installing it would make every integrity check fail."),
            KeyboxVerdict.Expired => new Check("Result", CheckState.Fail, "Expired. It won't work."),
            _ => new Check("Result", CheckState.Fail, "Not a usable keybox."),
        };
        var rows = new List<(Check, UIElement?)> { (verdict, null) };
        rows.AddRange(_report.Findings.Select(f => (new Check("Detail", CheckState.Pass, f) with
        {
            State = f.Contains("REVOKED") || f.Contains("SUSPENDED") || f.Contains("does not") || f.Contains("expired") ? CheckState.Fail
                  : f.Contains("not checked") || f.Contains("expires soon") ? CheckState.Warn : CheckState.Pass,
        }, (UIElement?)null)));
        CheckList.Render(ReportRows, rows);
        UpdateInstallButton();
    }

    void UpdateInstallButton() => InstallButton.IsEnabled = _report is { Usable: true } && Serial is not null && !_app.Busy;

    async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_report is not { Usable: true } || _file is null || Serial is not { } s || _app.Adb is null) return;
        InstallBusy.IsActive = true;
        InstallButton.IsEnabled = false;
        try
        {
            await new PifToolbox(_app.Adb, _app.Log).SwapKeyboxAsync(s, _file, _report, CancellationToken.None);
            Show(InfoBarSeverity.Success, "Keybox installed",
                "No restart needed. Run the integrity check again on the phone (Play Store › Settings › About › Play Store version, tap 7 times, then Developer options › Check integrity).");
        }
        catch (Exception ex)
        {
            Show(InfoBarSeverity.Error, "Couldn't install the keybox", ex.Message);
        }
        InstallBusy.IsActive = false;
        await RefreshStatusAsync();
    }

    void Show(InfoBarSeverity sev, string title, string message)
    {
        Message.Severity = sev;
        Message.Title = title;
        Message.Message = message;
        Message.IsOpen = true;
    }
}
