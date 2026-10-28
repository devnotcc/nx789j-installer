using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NxInstaller.App.Services;
using NxInstaller.Core.Install;
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
    public bool CanContinue => !_app.WipeRequired || WipeConfirm.IsChecked == true || Dry.IsOn;
    public event EventHandler? StateChanged;

    PackageManifest? Manifest => _app.Selected?.Package?.Manifest;

    void Load()
    {
        _loading = true;
        var m = Manifest;
        Title.Text = m is null ? "Ready to install" : $"Ready to install {m.Name}";

        var wipe = _app.WipeRequired;
        WipeCard.Visibility = wipe ? Visibility.Visible : Visibility.Collapsed;
        KeepCard.Visibility = wipe || m?.Kind == PackageKind.Recovery ? Visibility.Collapsed : Visibility.Visible;
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
        RenderWrites();
        StateChanged?.Invoke(this, EventArgs.Empty);
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
