using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NxInstaller.App.Services;
using NxInstaller.Core.Device;

namespace NxInstaller.App.Views;

public sealed partial class RecoveryPage : Page
{
    readonly AppServices _app = AppServices.Current;

    public RecoveryPage() => InitializeComponent();

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

    void Show(InfoBarSeverity sev, string title, string message)
    {
        Message.Severity = sev;
        Message.Title = title;
        Message.Message = message;
        Message.IsOpen = true;
    }
}
