using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NxInstaller.App.Views;

public sealed partial class WelcomePage : Page, IWizardPage
{
    public WelcomePage() => InitializeComponent();

    public string PrimaryText => "Get started";
    public bool CanContinue => Understood.IsChecked == true;
    public event EventHandler? StateChanged;

    void Understood_Changed(object sender, RoutedEventArgs e) => StateChanged?.Invoke(this, EventArgs.Empty);
}
