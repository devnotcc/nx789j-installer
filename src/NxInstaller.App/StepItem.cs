using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace NxInstaller.App;

public enum StepState { Upcoming, Current, Done }

/// <summary>One entry of the step rail: a numbered circle that turns into a check once done.</summary>
public sealed partial class StepItem(int index, string title, bool last) : ObservableObject
{
    public int Index { get; } = index;
    public string Number { get; } = (index + 1).ToString();
    public string Title { get; } = title;
    public Visibility LineVisibility { get; } = last ? Visibility.Collapsed : Visibility.Visible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CircleFill), nameof(CircleStroke), nameof(NumberBrush), nameof(NumberVisibility),
        nameof(CheckVisibility), nameof(Weight), nameof(TextOpacity), nameof(LineBrush))]
    public partial StepState State { get; set; }

    static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public Brush CircleFill => State == StepState.Done ? Res("AccentFillColorDefaultBrush") : Res("SubtleFillColorTransparentBrush");
    public Brush CircleStroke => State == StepState.Upcoming ? Res("ControlStrongStrokeColorDefaultBrush") : Res("AccentFillColorDefaultBrush");
    public Brush NumberBrush => State == StepState.Current ? Res("AccentTextFillColorPrimaryBrush") : Res("TextFillColorSecondaryBrush");
    public Brush LineBrush => State == StepState.Done ? Res("AccentFillColorDefaultBrush") : Res("ControlStrongStrokeColorDefaultBrush");
    public Visibility NumberVisibility => State == StepState.Done ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CheckVisibility => State == StepState.Done ? Visibility.Visible : Visibility.Collapsed;
    public FontWeight Weight => State == StepState.Current ? FontWeights.SemiBold : FontWeights.Normal;
    public double TextOpacity => State == StepState.Upcoming ? 0.6 : 1.0;
}
