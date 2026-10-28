using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NxInstaller.Core.Safety;
using Windows.Storage.Pickers;

namespace NxInstaller.App.Views;

/// <summary>Renders a list of checks as rows with a status icon, like Windows setup's compatibility list.</summary>
public static class CheckList
{
    public static void Render(Panel host, IEnumerable<(Check Check, UIElement? Action)> rows)
    {
        host.Children.Clear();
        foreach (var (c, action) in rows) host.Children.Add(Row(c, action));
    }

    public static Border Row(Check c, UIElement? action = null)
    {
        var (glyph, brush) = c.State switch
        {
            CheckState.Pass => ("\uE73E", "SystemFillColorSuccessBrush"),
            CheckState.Warn => ("\uE7BA", "SystemFillColorCautionBrush"),
            _ => ("\uE711", "SystemFillColorCriticalBrush"),
        };
        var icon = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Top,
            Background = (Brush)Application.Current.Resources[brush],
            Child = new FontIcon { Glyph = glyph, FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"] },
        };
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = c.Name, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        text.Children.Add(new TextBlock { Text = c.Detail, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });

        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        if (action is FrameworkElement fe)
        {
            fe.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(fe, 2);
            grid.Children.Add(fe);
        }
        return new Border { Style = (Style)Application.Current.Resources["Card"], Padding = new Thickness(18, 14, 18, 14), Child = grid };
    }

    /// <summary>Folder picker that works in an unpackaged WinUI app (needs the window handle).</summary>
    public static async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    public static async Task<string?> PickFileAsync(params string[] extensions)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        foreach (var e in extensions) picker.FileTypeFilter.Add(e);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
        return (await picker.PickSingleFileAsync())?.Path;
    }
}
