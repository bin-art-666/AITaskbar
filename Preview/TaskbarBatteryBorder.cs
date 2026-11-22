using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace AITaskbar;

internal sealed class TaskbarBatteryBorder : Grid, IDisposable
{
    internal const int CapsuleWidth = 74, CapsuleHeight = 32;
    private static readonly Vector3 TideLow = new(-23, 4, 0), TideHigh = new(-8, -13, 0);
    private readonly ContainerVisual rootVisual, tideLayer;
    private readonly ShapeVisual visual;
    private readonly CompositionRoundedRectangleGeometry track, progress;
    private readonly CompositionSpriteShape progressLine;
    private readonly CompositionColorBrush trackBrush, levelBrush;
    private readonly SpriteVisual tide;
    private readonly CompositionRoundedRectangleGeometry tideBounds;
    private readonly CompositionGeometricClip tideClip;
    private readonly CompositionRadialGradientBrush tideBrush;
    private readonly CompositionColorGradientStop tideCenter, tideEdge, tideFade;
    private bool animated;

    internal TaskbarBatteryBorder()
    {
        IsHitTestVisible = false;
        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        rootVisual = compositor.CreateContainerVisual();
        rootVisual.Size = new(CapsuleWidth, CapsuleHeight);
        rootVisual.IsVisible = false;

        tideLayer = compositor.CreateContainerVisual();
        tideLayer.Size = rootVisual.Size;
        tideBounds = CreateCapsule(compositor, 4);
        tideClip = compositor.CreateGeometricClip(tideBounds);
        tideLayer.Clip = tideClip;
        tideLayer.IsVisible = false;
        tideBrush = compositor.CreateRadialGradientBrush();
        tideBrush.EllipseCenter = new(.5f, .79f);
        tideBrush.EllipseRadius = new(.57f, .83f);
        tideCenter = compositor.CreateColorGradientStop();
        tideEdge = compositor.CreateColorGradientStop();
        tideFade = compositor.CreateColorGradientStop();
        tideEdge.Offset = .48f;
        tideFade.Offset = 1;
        tideBrush.ColorStops.Add(tideCenter);
        tideBrush.ColorStops.Add(tideEdge);
        tideBrush.ColorStops.Add(tideFade);
        tide = compositor.CreateSpriteVisual();
        tide.Size = new(104, 45);
        tide.Offset = TideLow;
        tide.Brush = tideBrush;
        tideLayer.Children.InsertAtTop(tide);
        rootVisual.Children.InsertAtTop(tideLayer);

        visual = compositor.CreateShapeVisual();
        visual.Size = rootVisual.Size;
        (track, _, trackBrush) = CreateLine(compositor, 2.2f);
        (progress, progressLine, levelBrush) = CreateLine(compositor, 2.8f);
        rootVisual.Children.InsertAtTop(visual);
        ElementCompositionPreview.SetElementChildVisual(this, rootVisual);
    }

    private static CompositionRoundedRectangleGeometry CreateCapsule(Compositor compositor, float inset)
    {
        var geometry = compositor.CreateRoundedRectangleGeometry();
        geometry.Offset = new Vector2(inset);
        geometry.Size = new(CapsuleWidth - 2 * inset, CapsuleHeight - 2 * inset);
        geometry.CornerRadius = new Vector2(geometry.Size.Y / 2);
        return geometry;
    }

    private (CompositionRoundedRectangleGeometry, CompositionSpriteShape, CompositionColorBrush) CreateLine(
        Compositor compositor, float thickness)
    {
        var geometry = CreateCapsule(compositor, 1.4f);
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
        tideCenter.Color = Color.FromArgb(148, highlight.R, highlight.G, highlight.B);
        tideEdge.Color = Color.FromArgb(73, level.R, level.G, level.B);
        tideFade.Color = Color.FromArgb(0, level.R, level.G, level.B);
    }

    internal void Update(int? percentage, bool charging, bool animations)
    {
        rootVisual.IsVisible = percentage.HasValue;
        progressLine.StrokeBrush = percentage > 0 ? levelBrush : null;
        progress.TrimEnd = (percentage ?? 0) / 100f;
        bool animate = percentage.HasValue && charging && animations;
        if (animate == animated) return;
        animated = animate;
        tideLayer.IsVisible = animate;
        if (!animate)
        {
            tide.StopAnimation(nameof(Visual.Offset));
            tide.StopAnimation(nameof(Visual.Opacity));
            tide.Offset = TideLow;
            return;
        }
        var compositor = tide.Compositor;
        using var easing = compositor.CreateCubicBezierEasingFunction(new(.42f, 0), new(.58f, 1));
        using var rise = compositor.CreateVector3KeyFrameAnimation();
        using var fade = compositor.CreateScalarKeyFrameAnimation();
        rise.Duration = fade.Duration = TimeSpan.FromSeconds(4);
        rise.IterationBehavior = fade.IterationBehavior = AnimationIterationBehavior.Forever;
        rise.InsertKeyFrame(0, TideLow);
        rise.InsertKeyFrame(.5f, TideHigh, easing);
        rise.InsertKeyFrame(1, TideLow, easing);
        fade.InsertKeyFrame(0, .40f);
        fade.InsertKeyFrame(.5f, .78f, easing);
        fade.InsertKeyFrame(1, .40f, easing);
        tide.StartAnimation(nameof(Visual.Offset), rise);
        tide.StartAnimation(nameof(Visual.Opacity), fade);
    }

    public void Dispose()
    {
        tide.StopAnimation(nameof(Visual.Offset));
        tide.StopAnimation(nameof(Visual.Opacity));
        ElementCompositionPreview.SetElementChildVisual(this, null);
        foreach (var shape in visual.Shapes.ToArray()) shape.Dispose();
        track.Dispose();
        progress.Dispose();
        trackBrush.Dispose();
        levelBrush.Dispose();
        visual.Dispose();
        tide.Dispose();
        tideCenter.Dispose();
        tideEdge.Dispose();
        tideFade.Dispose();
        tideBrush.Dispose();
        tideClip.Dispose();
        tideBounds.Dispose();
        tideLayer.Dispose();
        rootVisual.Dispose();
    }
}
