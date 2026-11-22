using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace AITaskbar;

internal sealed class TaskbarBatteryBorder : Grid, IDisposable
{
    internal const int CapsuleWidth = 74, CapsuleHeight = 32;
    private readonly ShapeVisual visual;
    private readonly CompositionRoundedRectangleGeometry track, progress, sweep;
    private readonly CompositionSpriteShape progressLine, sweepLine;
    private readonly CompositionColorBrush trackBrush, levelBrush, sweepBrush;
    private bool animated;

    internal TaskbarBatteryBorder()
    {
        IsHitTestVisible = false;
        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        visual = compositor.CreateShapeVisual();
        visual.Size = new(CapsuleWidth, CapsuleHeight);
        visual.IsVisible = false;
        (track, _, trackBrush) = CreateLine(compositor, 1.25f);
        (progress, progressLine, levelBrush) = CreateLine(compositor, 1.6f);
        (sweep, sweepLine, sweepBrush) = CreateLine(compositor, 2.2f);
        sweep.TrimEnd = .12f;
        ElementCompositionPreview.SetElementChildVisual(this, visual);
    }

    private (CompositionRoundedRectangleGeometry, CompositionSpriteShape, CompositionColorBrush) CreateLine(
        Compositor compositor, float thickness)
    {
        var geometry = compositor.CreateRoundedRectangleGeometry();
        geometry.Offset = new Vector2(2);
        geometry.Size = new(CapsuleWidth - 4, CapsuleHeight - 4);
        geometry.CornerRadius = new Vector2((CapsuleHeight - 4) / 2f);
        var shape = compositor.CreateSpriteShape(geometry);
        var brush = compositor.CreateColorBrush();
        shape.StrokeBrush = brush;
        shape.StrokeThickness = thickness;
        shape.StrokeStartCap = shape.StrokeEndCap = CompositionStrokeCap.Round;
        visual.Shapes.Add(shape);
        return (geometry, shape, brush);
    }

    internal void ApplyColors(Color track, Color level, Color highlight)
    {
        trackBrush.Color = track;
        levelBrush.Color = level;
        sweepBrush.Color = highlight;
    }

    internal void Update(int? percentage, bool charging, bool animations)
    {
        visual.IsVisible = percentage.HasValue;
        progressLine.StrokeBrush = percentage > 0 ? levelBrush : null;
        progress.TrimEnd = (percentage ?? 0) / 100f;
        bool showSweep = percentage.HasValue && charging;
        sweepLine.StrokeBrush = showSweep ? sweepBrush : null;
        bool animate = showSweep && animations;
        if (animate == animated) return;
        animated = animate;
        sweep.StopAnimation(nameof(CompositionGeometry.TrimOffset));
        sweep.TrimOffset = 0;
        if (!animate) return;
        var compositor = visual.Compositor;
        using var animation = compositor.CreateScalarKeyFrameAnimation();
        using var linear = compositor.CreateLinearEasingFunction();
        animation.InsertKeyFrame(0, 0, linear);
        animation.InsertKeyFrame(1, 1, linear);
        animation.Duration = TimeSpan.FromMilliseconds(2560);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        sweep.StartAnimation(nameof(CompositionGeometry.TrimOffset), animation);
    }

    public void Dispose()
    {
        sweep.StopAnimation(nameof(CompositionGeometry.TrimOffset));
        ElementCompositionPreview.SetElementChildVisual(this, null);
        foreach (var shape in visual.Shapes.ToArray()) shape.Dispose();
        track.Dispose();
        progress.Dispose();
        sweep.Dispose();
        trackBrush.Dispose();
        levelBrush.Dispose();
        sweepBrush.Dispose();
        visual.Dispose();
    }
}
