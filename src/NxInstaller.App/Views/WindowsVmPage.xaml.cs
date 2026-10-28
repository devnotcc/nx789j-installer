using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NxInstaller.App.Services;
using NxInstaller.Core.Device;
using NxInstaller.Core.Safety;

namespace NxInstaller.App.Views;

/// <summary>
/// Placeholder for the VM setup: shows the requirements and lets the user pick the ISO. The setup
/// itself (crosvm, the GPU driver package, the disk image) lands with the Debian package.
/// </summary>
public sealed partial class WindowsVmPage : Page
{
    readonly AppServices _app = AppServices.Current;

    public WindowsVmPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Render();
    }

    void Render()
    {
        var p = _app.Phone;
        var rows = new List<Check>
        {
            p.Os == PhoneOs.Debian
                ? new Check("Debian", CheckState.Pass, "The phone runs Debian.")
                : new Check("Debian", CheckState.Fail, "Install Debian 13 with KDE Plasma first; the VM runs on top of it."),
            new Check("Free space on the phone", CheckState.Warn, "Windows needs about 64 GB on the phone."),
            new Check("Memory", CheckState.Pass, "The VM gets 8 GB of the phone's memory."),
        };
        CheckList.Render(Rows, rows.Select(r => (r, (UIElement?)null)));
    }

    async void Choose_Click(object sender, RoutedEventArgs e)
    {
        if (await CheckList.PickFileAsync(".iso") is { } iso) IsoName.Text = Path.GetFileName(iso);
    }
}
