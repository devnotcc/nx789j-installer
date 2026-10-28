using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NxInstaller.App.Services;
using NxInstaller.Core;
using NxInstaller.Core.Device;
using NxInstaller.Core.Edl;
using NxInstaller.Core.Install;
using NxInstaller.Core.Packages;

namespace NxInstaller.App.Views;

public sealed partial class RecoveryPage : Page
{
    readonly AppServices _app = AppServices.Current;

    public RecoveryPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadPackagesAsync();
    }

    async Task LoadPackagesAsync()
    {
        await _app.RefreshPackagesAsync();
        KernelPackage.Items.Clear();
        foreach (var c in _app.Catalog.Where(c => c.Package is { Manifest.Kind: PackageKind.Android } p
                                                 && p.Manifest.Images.Any(i => i.Partition is "boot" or "init_boot")))
            KernelPackage.Items.Add(new ComboBoxItem { Content = $"{c.Package!.Manifest.Name} {c.Package.Manifest.Version}", Tag = c.Package });
        // preselect the OS the phone runs, when it is in the list
        var running = _app.CurrentFamily;
        KernelPackage.SelectedItem = KernelPackage.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => ((PackageEntry)i.Tag).Manifest.Family == running) ?? KernelPackage.Items.FirstOrDefault();
    }

    async void Reboot_Click(object sender, RoutedEventArgs e)
    {
        var target = (string)((Button)sender).Tag;
        var p = _app.Phone;
        if (p.Mode is not (PhoneMode.Android or PhoneMode.Recovery) || p.Serial is null || _app.Adb is null)
        {
            Show(InfoBarSeverity.Warning, "Phone not reachable", "Boot it with USB debugging on (or into TWRP) and try again. Nubia's fastboot can't restart into other modes.");
            return;
        }
        var r = await _app.Adb.RunAsync(p.Serial, target.Length == 0 ? ["reboot"] : ["reboot", target], TimeSpan.FromSeconds(30));
        if (r.Ok) Show(InfoBarSeverity.Success, "Restarting", target.Length == 0 ? "Into the system." : $"Into {target}.");
        else Show(InfoBarSeverity.Error, "Couldn't restart", r.Output.Trim());
    }

    async void Repair_Click(object sender, RoutedEventArgs e)
    {
        if (_app.EdlTools is not { } tools) { Show(InfoBarSeverity.Error, "EDL tools missing", "Set the ZTE toolbox folder on the This PC step first."); return; }
        var dry = Dry.IsChecked == true;
        await RunAsync("Repairing the slot", async ct =>
        {
            var edl = new EdlSession(tools, _app.Run, _app.Log, Path.Combine(AppPaths.Work, "edl")) { DryRun = dry };
            Step("Waiting for the phone in EDL mode (power off, hold both volume buttons, plug in)");
            var port = EdlSession.FindPort() ?? await edl.WaitForPortAsync(TimeSpan.FromMinutes(5), ct);
            Step("Reading the partition tables");
            await edl.ConnectAsync(port, ct);
            var slot = InstallPlanner.ActiveSlot(edl.Table!);
            Step($"Checking slot {slot}");
            var changed = await edl.RepairSlotAsync(slot, ct);
            Step("Restarting the phone");
            await edl.ResetAsync(ct);
            return changed
                ? (dry ? $"Test run: slot {slot} is marked unbootable and would be repaired. Nothing was written." : $"Slot {slot} can start again. The phone is restarting.")
                : $"Slot {slot} wasn't marked unbootable, so nothing was changed.";
        });
    }

    async void Kernel_Click(object sender, RoutedEventArgs e)
    {
        if (_app.EdlTools is not { } tools || _app.Adb is not { } adb) { Show(InfoBarSeverity.Error, "Tools missing", "Finish the This PC step first."); return; }
        if (KernelPackage.SelectedItem is not ComboBoxItem { Tag: PackageEntry entry }) { Show(InfoBarSeverity.Warning, "Pick a package", "Choose the OS that is installed on the phone."); return; }
        var dry = Dry.IsChecked == true;
        if (!dry)
        {
            var ok = await new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "Put the kernel back?",
                Content = $"boot and init_boot are replaced with the ones from {entry.Manifest.Name} {entry.Manifest.Version}. The current ones are saved to this PC first. Apps and data stay.",
                PrimaryButtonText = "Put it back", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
            }.ShowAsync();
            if (ok != ContentDialogResult.Primary) return;
        }
        var serial = _app.Phone.Mode == PhoneMode.Android ? _app.Phone.Serial : null;
        await RunAsync("Putting the kernel back", async ct =>
        {
            var inst = new Installer(_app.Log, _app.Run, adb, tools);
            inst.Step += s => Step(s.Title);
            if (serial is null) Step("Waiting for the phone in EDL mode (power off, hold both volume buttons, plug in)");
            await inst.InstallAsync(entry, Installer.KernelOnly(dry), serial, ct);
            return dry ? "Test run finished; nothing was written." : "The kernel is back. The phone is restarting.";
        });
    }

    async Task RunAsync(string title, Func<CancellationToken, Task<string>> work)
    {
        _app.Busy = true;
        RepairButton.IsEnabled = KernelButton.IsEnabled = false;
        ProgressArea.Visibility = Visibility.Visible;
        Message.IsOpen = false;
        StepText.Text = title;
        try
        {
            var result = await Task.Run(() => work(CancellationToken.None));
            Show(InfoBarSeverity.Success, "Done", result);
        }
        catch (Exception ex)
        {
            _app.Log.Error(ex.ToString());
            Show(InfoBarSeverity.Error, "Didn't finish", ex.Message);
        }
        finally
        {
            _app.Busy = false;
            RepairButton.IsEnabled = KernelButton.IsEnabled = true;
            ProgressArea.Visibility = Visibility.Collapsed;
        }
    }

    void Step(string text) => DispatcherQueue.TryEnqueue(() => StepText.Text = text);

    void Show(InfoBarSeverity sev, string title, string message)
    {
        Message.Severity = sev;
        Message.Title = title;
        Message.Message = message;
        Message.IsOpen = true;
    }
}
