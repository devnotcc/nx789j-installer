using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NxInstaller.App.Services;
using NxInstaller.Core.Device;
using NxInstaller.Core.Safety;

namespace NxInstaller.App.Views;

public sealed partial class PhonePage : Page, IWizardPage
{
    readonly AppServices _app = AppServices.Current;
    List<Check> _checks = [];

    public PhonePage()
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
        if (e.PropertyName == nameof(AppServices.Phone)) DispatcherQueue.TryEnqueue(Refresh);
    }

    void Refresh()
    {
        var s = _app.Phone;
        _checks = DeviceGate.CheckPhone(s, _app.Selected?.Package?.Manifest);
        CheckList.Render(Rows, _checks.Select(c => (c, (UIElement?)null)));
        HowTo.Visibility = s.Mode is PhoneMode.None or PhoneMode.Unauthorized ? Visibility.Visible : Visibility.Collapsed;

        Details.Children.Clear();
        Details.RowDefinitions.Clear();
        var info = new List<(string, string?)>
        {
            ("Mode", s.Mode.ToString()),
            ("Running", s.Mode == PhoneMode.Android ? Running(s) : null),
            ("Slot", s.Slot),
            ("Kernel", s.Kernel),
            ("Firmware", s.Baseband),
            ("Root", s.Mode == PhoneMode.Android ? (s.Root ? "Yes" : "No") : null),
            ("Serial", s.Serial),
        }.Where(r => r.Item2 is not null).ToList();
        for (var i = 0; i < info.Count; i++)
        {
            Details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var k = new TextBlock { Text = info[i].Item1, Opacity = 0.65 };
            var v = new TextBlock { Text = info[i].Item2, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(k, i); Grid.SetRow(v, i); Grid.SetColumn(v, 1);
            Details.Children.Add(k); Details.Children.Add(v);
        }
        Details.Visibility = info.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"Evolution X 16.0-..." without repeating the name when the build string already has it.</summary>
    static string Running(PhoneSnapshot s)
    {
        var name = MainWindow.OsName(s.Os);
        if (s.OsVersion is not { } v) return name;
        return v.Replace(" ", "").Contains(name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase) ? v : $"{name} {v}";
    }
}
