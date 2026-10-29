using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NxInstaller.App.Services;
using NxInstaller.Core.Install;
using NxInstaller.Core.Linux;
using NxInstaller.Core.Packages;

namespace NxInstaller.App.Views;

public sealed partial class OptionsPage : Page, IWizardPage
{
    readonly AppServices _app = AppServices.Current;
    bool _loading;

    public OptionsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Load();
    }

    public string PrimaryText => Dry.IsOn ? "Start test run" : "Next";
    public bool CanContinue => _app.IsLinux
        ? _app.LinuxPasswordHash is not null || Dry.IsOn
        : !_app.WipeRequired || WipeConfirm.IsChecked == true || Dry.IsOn;

    const int MinPassword = 4;
    public event EventHandler? StateChanged;

    PackageManifest? Manifest => _app.Selected?.Package?.Manifest;

    void Load()
    {
        _loading = true;
        var m = Manifest;
        Title.Text = m is null ? "Ready to install" : $"Ready to install {m.Name}";

        var wipe = _app.WipeRequired;
        WipeCard.Visibility = wipe ? Visibility.Visible : Visibility.Collapsed;
        KeepCard.Visibility = wipe || m?.Kind != PackageKind.Android ? Visibility.Collapsed : Visibility.Visible;
        LinuxCard.Visibility = _app.IsLinux ? Visibility.Visible : Visibility.Collapsed;
        DiskGb.Value = _app.LinuxDiskGb;
        ReplaceLinux.IsOn = _app.ReplaceLinux;
        BootLinux.IsOn = _app.BootLinux;
        // the password is never kept on this page: going back means typing it again
        _app.LinuxPasswordHash = null;
        LinuxPassword.Password = LinuxPassword2.Password = "";
        PasswordHint.Text = $"At least {MinPassword} characters.";
        var current = MainWindow.OsName(_app.Phone.Os);
        WipeReason.Text = _app.CurrentFamily is null
            ? $"The phone runs {current}. {m?.Name} uses different encryption, so the data partition is formatted."
            : $"Moving from {current} to {m?.Name} needs a clean data partition.";
        KeepReason.Text = $"The phone already runs {current}, so this is an update. Turn this on to start fresh.";
        WipeConfirm.IsChecked = _app.WipeConfirmed;
        EraseAnyway.IsOn = _app.EraseAnyway;

        var hasTwrp = m?.Images.Any(i => i.Option == "twrp") == true;
        TwrpRow.Visibility = TwrpLine.Visibility = hasTwrp ? Visibility.Visible : Visibility.Collapsed;
        Twrp.IsOn = _app.IncludeTwrp;
        Backup.IsOn = _app.BackupFirst;
        Dry.IsOn = _app.DryRun;
        _loading = false;
        RenderWrites();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    void Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.WipeConfirmed = WipeConfirm.IsChecked == true;
        _app.EraseAnyway = EraseAnyway.IsOn;
        _app.IncludeTwrp = Twrp.IsOn;
        _app.BackupFirst = Backup.IsOn;
        _app.DryRun = Dry.IsOn;
        _app.ReplaceLinux = ReplaceLinux.IsOn;
        _app.BootLinux = BootLinux.IsOn;
        RenderWrites();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    void Password_Changed(object sender, RoutedEventArgs e)
    {
        var a = LinuxPassword.Password;
        var ok = a.Length >= MinPassword && a == LinuxPassword2.Password;
        _app.LinuxPasswordHash = ok ? Sha512Crypt.Hash(a) : null;
        PasswordHint.Text = a.Length < MinPassword ? $"At least {MinPassword} characters."
            : LinuxPassword2.Password.Length == 0 ? "Type it again below."
            : ok ? "The passwords match." : "The passwords don't match.";
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    void DiskGb_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_loading || double.IsNaN(sender.Value)) return;
        _app.LinuxDiskGb = Math.Round(sender.Value);
        RenderWrites();
    }

    void RenderWrites()
    {
        WriteList.Children.Clear();
        if (Manifest is not { } m) return;
        var o = new InstallOptions { Options = _app.IncludeTwrp ? ["twrp"] : [] };
        foreach (var img in Installer.Selected(m, o))
        {
            var label = img.Slot == SlotTarget.Active ? $"{img.Partition} (active slot)" : img.Partition;
            WriteList.Children.Add(Line(label, Size(img.Size)));
        }
        if (_app.WipeRequired || _app.EraseAnyway) WriteList.Children.Add(Line("metadata, userdata", "erased"));
        foreach (var f in m.Files) WriteList.Children.Add(Line($"{LinuxAgent.InstallDir}/{f.File}", Size(f.Size)));
        if (m.Kind == PackageKind.Linux)
            WriteList.Children.Add(Line($"{LinuxAgent.InstallDir}/{LinuxAgent.Image} grown to", $"{_app.LinuxDiskGb:0} GB"));
        WriteList.Children.Add(new TextBlock
        {
            Text = "Firmware (bootloaders, modem, TrustZone) and the bootloader unlock are never written.",
            Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        });
    }

    static Grid Line(string a, string b)
    {
        var g = new Grid();
        g.Children.Add(new TextBlock { Text = a, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas") });
        g.Children.Add(new TextBlock { Text = b, HorizontalAlignment = HorizontalAlignment.Right, Opacity = 0.7 });
        return g;
    }

    static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB" : $"{bytes / (double)(1 << 20):0} MB";
}
