namespace NxInstaller.App.Views;

/// <summary>A page of the setup wizard. The main window owns the Back/Next buttons and asks the page.</summary>
public interface IWizardPage
{
    string PrimaryText { get; }
    bool CanContinue { get; }
    /// <summary>Raised when <see cref="CanContinue"/> or <see cref="PrimaryText"/> changes.</summary>
    event EventHandler? StateChanged;
    /// <summary>Called on Next; return false to stay on the page (e.g. a confirmation was declined).</summary>
    Task<bool> OnNextAsync() => Task.FromResult(true);
    /// <summary>The install page hides Back while it runs.</summary>
    bool CanGoBack => true;
}
