using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace NxInstaller.App.Views;

public sealed partial class ToolsPage : Page
{
    static readonly (string Glyph, string Title, string Text, Type? Page)[] Tools =
    [
        ("\uE72E", "Play Integrity", "Check a keybox against Google's revocation list and install it, and see the status of the integrity modules.", typeof(PifPage)),
        ("\uE8F1", "Back up every partition", "Save all of the phone's partitions to this PC over EDL. Read-only: nothing on the phone changes.", typeof(BackupPage)),
        ("\uE90F", "Recovery and restart", "Restart into recovery, EDL or the bootloader, and repair a phone that won't start.", typeof(RecoveryPage)),
        ("\uE7F4", "Windows 11 virtual machine", "Run Windows 11 for ARM inside Debian on the phone, with GPU acceleration.", typeof(WindowsVmPage)),
        ("\uE785", "Unlock the bootloader", "Coming later.", null),
    ];

    public ToolsPage()
    {
        InitializeComponent();
        foreach (var t in Tools) Cards.Children.Add(Card(t));
    }

    static UIElement Card((string Glyph, string Title, string Text, Type? Page) t)
    {
        var res = Application.Current.Resources;
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new FontIcon { Glyph = t.Glyph, FontSize = 20, Foreground = (Brush)res["AccentTextFillColorPrimaryBrush"], VerticalAlignment = VerticalAlignment.Center };
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = t.Title, Style = (Style)res["BodyStrongTextBlockStyle"] });
        text.Children.Add(new TextBlock { Text = t.Text, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        var chevron = new FontIcon { Glyph = "\uE76C", FontSize = 12, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(chevron, 2);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        grid.Children.Add(chevron);

        var b = new Button
        {
            Style = (Style)res["CardButton"], Content = grid,
            Padding = new Thickness(20, 16, 20, 16), IsEnabled = t.Page is not null,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, t.Title);
        if (t.Page is { } page) b.Click += (_, _) => App.Window?.ShowTool(page);
        return b;
    }
}
