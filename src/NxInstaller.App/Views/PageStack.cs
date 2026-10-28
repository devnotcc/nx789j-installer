using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace NxInstaller.App.Views;

/// <summary>
/// The column every page's content sits in: as wide as the window allows up to its MaxWidth, and
/// left-aligned, like the Windows Settings app. A plain left-aligned StackPanel would size to its
/// widest child instead, so pages with short text came out narrower than the rest.
/// </summary>
public sealed partial class PageStack : StackPanel
{
    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? MaxWidth : Math.Min(available.Width, MaxWidth);
        var size = base.MeasureOverride(new Size(width, available.Height));
        return new Size(width, size.Height);
    }
}
