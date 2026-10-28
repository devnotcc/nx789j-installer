using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NxInstaller.App.Services;
using NxInstaller.Core;

namespace NxInstaller.App.Views;

public sealed partial class AboutPage : Page
{
    readonly AppServices _app = AppServices.Current;

    public AboutPage()
    {
        InitializeComponent();
        Version.Text = $"Version {Net.Version} · Preview";
        Repo.Text = _app.Settings.Repository;
    }

    void Logs_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(AppPaths.Root, "logs");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    void Repo_Click(object sender, RoutedEventArgs e)
    {
        var v = Repo.Text.Trim();
        if (v.Count(c => c == '/') != 1) return;
        _app.Settings.Repository = v;
        _app.Settings.Save();
    }
}
