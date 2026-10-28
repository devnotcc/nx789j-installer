using System.ComponentModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using NxInstaller.App.Services;
using NxInstaller.App.Views;
using NxInstaller.Core.Device;

namespace NxInstaller.App;

public sealed partial class MainWindow : Window
{
    static readonly (string Title, Type Page)[] Steps =
    [
        ("Welcome", typeof(WelcomePage)),
        ("Choose OS", typeof(ChooseOsPage)),
        ("This PC", typeof(ThisPcPage)),
        ("Your phone", typeof(PhonePage)),
        ("Options", typeof(OptionsPage)),
        ("Install", typeof(InstallPage)),
    ];

    readonly List<StepItem> _steps;
    int _current;
    IWizardPage? _page;
    readonly AppServices _app = AppServices.Current;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 720));
        if (AppWindow.Presenter is OverlappedPresenter p) { p.PreferredMinimumWidth = 940; p.PreferredMinimumHeight = 620; }

        _steps = Steps.Select((s, i) => new StepItem(i, s.Title, i == Steps.Length - 1)).ToList();
        StepList.ItemsSource = _steps;
        ContentFrame.Navigated += Frame_Navigated;

        _app.PropertyChanged += App_PropertyChanged;
        UpdatePhoneCard();
        GoTo(0);
        _ = _app.StartAsync(DispatcherQueue);
    }

    void GoTo(int index, bool back = false)
    {
        _current = index;
        for (var i = 0; i < _steps.Count; i++)
            _steps[i].State = i < index ? StepState.Done : i == index ? StepState.Current : StepState.Upcoming;
        StepCounter.Text = $"Step {index + 1} of {_steps.Count}";
        BottomBar.Visibility = Visibility.Visible;
        ToolsBar.Visibility = Visibility.Collapsed;
        ContentFrame.Navigate(Steps[index].Page, null, new SlideNavigationTransitionInfo
        {
            Effect = back ? SlideNavigationTransitionEffect.FromLeft : SlideNavigationTransitionEffect.FromRight,
        });
    }

    void Frame_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        if (_page is not null) _page.StateChanged -= Page_StateChanged;
        _page = e.Content as IWizardPage;
        if (_page is not null) _page.StateChanged += Page_StateChanged;
        UpdateButtons();
    }

    void Page_StateChanged(object? sender, EventArgs e) => UpdateButtons();

    void UpdateButtons()
    {
        if (_page is null) return;
        NextButton.Content = _page.PrimaryText;
        NextButton.IsEnabled = _page.CanContinue;
        BackButton.Visibility = _current > 0 && _page.CanGoBack ? Visibility.Visible : Visibility.Collapsed;
    }

    async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_page is null) return;
        NextButton.IsEnabled = false;
        try
        {
            if (!await _page.OnNextAsync()) return;
            if (_current < _steps.Count - 1) GoTo(_current + 1);
        }
        finally { UpdateButtons(); }
    }

    void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_current > 0) GoTo(_current - 1, back: true);
    }

    /// <summary>Clicking a finished step goes back to it; later steps can't be skipped to.</summary>
    void Step_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_app.Busy || sender is not FrameworkElement { Tag: int i }) return;
        if (i < _current || ToolsBar.Visibility == Visibility.Visible && i <= _current) GoTo(i, back: true);
    }

    void Tools_Click(object sender, RoutedEventArgs e) => ShowTool(typeof(ToolsPage));
    void About_Click(object sender, RoutedEventArgs e) => ShowTool(typeof(AboutPage));

    public void ShowTool(Type page, object? parameter = null)
    {
        if (_app.Busy) return;
        BottomBar.Visibility = Visibility.Collapsed;
        ToolsBar.Visibility = Visibility.Visible;
        ContentFrame.Navigate(page, parameter, new EntranceNavigationTransitionInfo());
    }

    void BackToSetup_Click(object sender, RoutedEventArgs e) => GoTo(_current, back: true);

    void App_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppServices.Phone) or nameof(AppServices.ToolsReady))
            DispatcherQueue.TryEnqueue(UpdatePhoneCard);
        if (e.PropertyName == nameof(AppServices.Busy))
            DispatcherQueue.TryEnqueue(() =>
            {
                ToolsButton.IsEnabled = AboutButton.IsEnabled = !_app.Busy;
                UpdateButtons();
            });
    }

    void UpdatePhoneCard()
    {
        var s = _app.Phone;
        (PhoneTitle.Text, PhoneDetail.Text) = !_app.ToolsReady ? ("Getting ready", "Downloading adb from Google…")
            : s.MoreThanOne ? ("More than one phone", "Connect only one")
            : s.Mode switch
            {
                PhoneMode.None => ("No phone yet", "Connect it with a USB data cable"),
                PhoneMode.Unauthorized => ("Waiting for approval", "Allow USB debugging on the phone"),
                PhoneMode.Edl => ("Phone in EDL mode", $"COM{s.EdlPort}"),
                PhoneMode.Fastboot => ("Phone in fastboot", s.Model ?? "Bootloader"),
                PhoneMode.Recovery => ("Phone in recovery", s.Os == PhoneOs.Twrp ? $"TWRP {s.OsVersion}" : "Recovery"),
                PhoneMode.Sideload => ("Phone in sideload", s.Serial ?? ""),
                _ => (s.IsSupportedModel ? "REDMAGIC 10 Pro" : s.Model ?? "Unknown phone",
                      $"{OsName(s.Os)}{(s.Battery is { } b ? $" · {b}%" : "")}"),
            };
        PhoneIcon.Glyph = s.Mode == PhoneMode.None ? "\uE8EA" : s.Mode == PhoneMode.Edl ? "\uE7F4" : "\uE8EA";
        PhoneIcon.Opacity = s.Mode == PhoneMode.None ? 0.5 : 1;
    }

    public static string OsName(PhoneOs os) => os switch
    {
        PhoneOs.Stock => "Stock RedMagic OS",
        PhoneOs.EvolutionX => "Evolution X",
        PhoneOs.LineageOS => "LineageOS",
        PhoneOs.Twrp => "TWRP",
        PhoneOs.Debian => "Debian",
        _ => "Android",
    };
}
