using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace Furchive.Infrastructure;

/// <summary>
/// Minimal GridLength animation to animate ColumnDefinition.Width and RowDefinition.Height.
/// Only supports pixel (Absolute) GridLength values.
/// </summary>
public class GridLengthAnimation : AnimationTimeline
{
    public override Type TargetPropertyType => typeof(GridLength);

    public static readonly DependencyProperty FromProperty = DependencyProperty.Register(
        nameof(From), typeof(GridLength?), typeof(GridLengthAnimation));

    public static readonly DependencyProperty ToProperty = DependencyProperty.Register(
        nameof(To), typeof(GridLength?), typeof(GridLengthAnimation));

    public GridLength? From
    {
        get => (GridLength?)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }

    public GridLength? To
    {
        get => (GridLength?)GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }

    public IEasingFunction? EasingFunction { get; set; }

    protected override Freezable CreateInstanceCore() => new GridLengthAnimation();

    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
    {
        double fromVal = (From?.Value) ?? ((GridLength)defaultOriginValue).Value;
        double toVal = (To?.Value) ?? ((GridLength)defaultDestinationValue).Value;

        double progress = animationClock.CurrentProgress ?? 0.0;
        if (EasingFunction != null)
        {
            progress = EasingFunction.Ease(progress);
        }

        double current = fromVal + ((toVal - fromVal) * progress);
        return new GridLength(Math.Max(0, current), GridUnitType.Pixel);
    }
}
