using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace AITaskbar;

internal sealed class TaskbarAttentionIndicator : Grid
{
    private const double BadgeDiameter = 10, BadgeCornerInset = 3.5;
    internal const double BadgeOverhang = BadgeDiameter / 2 - BadgeCornerInset;
    private const double RingGap = 1, RingWidth = 1.5, HaloWidth = 1;
    internal const double RingOverhang = RingGap + RingWidth + HaloWidth;
    private static readonly Color Amber = Color.FromArgb(255, 255, 179, 55);
    private static readonly Color Red = Color.FromArgb(255, 245, 35, 56);
    private readonly Grid rings = new() { Opacity = 0 };
    private Storyboard? pulse;

    internal TaskbarAttentionIndicator(double width, double height, double radius)
    {
        IsHitTestVisible = false;
        Width = width + 2 * RingOverhang;
        Height = height + 2 * RingOverhang;
        rings.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(radius + RingOverhang),
            BorderThickness = new Thickness(HaloWidth),
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, Amber.R, Amber.G, Amber.B))
        });
        rings.Children.Add(new Border
        {
            Margin = new Thickness(HaloWidth),
            CornerRadius = new CornerRadius(radius + RingGap + RingWidth),
            BorderThickness = new Thickness(RingWidth), BorderBrush = new SolidColorBrush(Amber)
        });
        Children.Add(rings);
    }

    internal static Border CreateBadge() => new()
    {
        Width = BadgeDiameter, Height = BadgeDiameter, CornerRadius = new CornerRadius(BadgeDiameter / 2),
        Background = new SolidColorBrush(Red), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.White),
        BorderThickness = new Thickness(1), IsHitTestVisible = false
    };

    internal static void ColorBadge(Border badge, Color? highlight)
    {
        var brush = (SolidColorBrush)badge.Background;
        var color = highlight ?? Red;
        if (brush.Color != color) brush.Color = color;
    }

    internal static double BadgeLeft(double iconWidth) => iconWidth - BadgeDiameter + BadgeOverhang;

    internal void Pulse(Action completed)
    {
        StopPulse();
        var fade = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 0 });
        fade.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(200), Value = 1 });
        fade.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(550), Value = 0 });
        fade.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(800), Value = .72 });
        fade.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(1300), Value = 0 });
        Storyboard.SetTarget(fade, rings);
        Storyboard.SetTargetProperty(fade, nameof(Opacity));
        pulse = new Storyboard();
        pulse.Children.Add(fade);
        pulse.Completed += (_, _) => completed();
        pulse.Begin();
    }

    internal void StopPulse()
    {
        pulse?.Stop();
        pulse = null;
    }
}
