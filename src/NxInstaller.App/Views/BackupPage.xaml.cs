using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NxInstaller.App.Services;
using NxInstaller.Core;
using NxInstaller.Core.Backup;
using NxInstaller.Core.Device;
using NxInstaller.Core.Edl;

namespace NxInstaller.App.Views;

public sealed partial class BackupPage : Page
{
    readonly AppServices _app = AppServices.Current;
    string _dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NX789J backups");

    public BackupPage()
    {
        InitializeComponent();
        Dest.Text = _dest;
    }

    async void Change_Click(object sender, RoutedEventArgs e)
    {
        if (await CheckList.PickFolderAsync() is { } d) Dest.Text = _dest = d;
    }

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_app.EdlTools is null)
        {
            Show(InfoBarSeverity.Error, "EDL tools missing", "Set the ZTE toolbox folder on the This PC step first.");
            return;
        }
        var phone = _app.Phone;
        if (phone.Mode is not (PhoneMode.Android or PhoneMode.Edl))
        {
            Show(InfoBarSeverity.Warning, "Connect the phone", "Boot it to Android with USB debugging on, or put it in EDL mode.");
            return;
        }
        var ok = await new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Start the backup?",
            Content = "The phone restarts into EDL mode and restarts normally when the backup is done. Nothing is written to it.",
            PrimaryButtonText = "Start", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        }.ShowAsync();
        if (ok != ContentDialogResult.Primary) return;

        _app.Busy = true;
        StartButton.IsEnabled = false;
        ProgressArea.Visibility = Visibility.Visible;
        Message.IsOpen = false;
        var dir = Path.Combine(_dest, $"backup-{DateTime.Now:yyyyMMdd-HHmmss}");
        var includeSuper = Super.IsOn;
        try
        {
            await Task.Run(async () =>
            {
                var edl = new EdlSession(_app.EdlTools, _app.Run, _app.Log, Path.Combine(AppPaths.Work, "edl"));
                if (phone.Mode == PhoneMode.Android && phone.Serial is { } s)
                {
                    Step("Restarting into EDL mode", 0);
                    await _app.Adb!.RunAsync(s, ["reboot", "edl"], TimeSpan.FromSeconds(30));
                }
                var port = await edl.WaitForPortAsync(TimeSpan.FromMinutes(2), CancellationToken.None);
                Step("Reading the partition tables", 0);
                await edl.ConnectAsync(port, CancellationToken.None);
                var bk = new EdlBackup(edl, _app.Log) { IncludeSuper = includeSuper };
                bk.Progress += (name, p) => Step($"Copying {name}", p);
                await bk.RunAsync(dir, CancellationToken.None);
                Step("Restarting the phone", 1);
                await edl.ResetAsync(CancellationToken.None);
            });
            Show(InfoBarSeverity.Success, "Backup complete", dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _app.Log.Error(ex.ToString());
            Show(InfoBarSeverity.Error, "The backup didn't finish", ex.Message);
        }
        finally
        {
            _app.Busy = false;
            StartButton.IsEnabled = true;
            ProgressArea.Visibility = Visibility.Collapsed;
        }
    }

    void Step(string text, double p) => DispatcherQueue.TryEnqueue(() =>
    {
        StepText.Text = text;
        Bar.Value = p;
        Percent.Text = $"{p:P0}";
    });

    void Show(InfoBarSeverity sev, string title, string message)
    {
        Message.Severity = sev;
        Message.Title = title;
        Message.Message = message;
        Message.IsOpen = true;
    }
}
