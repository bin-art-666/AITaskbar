using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace AITaskbar;

internal sealed class AppCenterIcon : Grid
{
    private readonly Border plate;
    private readonly Grid marks = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border[] squares = new Border[4];
    private readonly UISettings ui = new();
    private readonly AccessibilitySettings accessibility = new();
    private AppCenterIconStyle style = new();
    private CompositionRoundedRectangleGeometry? edge;
    private CompositionColorBrush? stroke;
    private ShapeVisual? visual;
    private int size;
    private bool expanded, loaded;

    internal AppCenterIcon(int size)
    {
        plate = new Border { BorderThickness = new Thickness(1) };
        Children.Add(plate);
        Children.Add(marks);
        for (int index = 0; index < 2; index++)
        {
            marks.RowDefinitions.Add(new());
            marks.ColumnDefinitions.Add(new());
        }
        for (int index = 0; index < squares.Length; index++)
        {
            var square = squares[index] = new Border();
            SetRow(square, index / 2);
            SetColumn(square, index % 2);
            marks.Children.Add(square);
        }
        Loaded += (_, _) => { loaded = true; RefreshColors(); RefreshEdge(); };
        Unloaded += (_, _) => { loaded = false; RefreshEdge(); };
        ActualThemeChanged += (_, _) => RefreshColors();
        RefreshColors();
        SetSize(size);
    }

    internal void SetSize(int next)
    {
        size = next;
        Width = Height = size;
        RefreshGeometry();
    }

    internal void Apply(AppCenterIconStyle next)
    {
        if (next == style) return;
        bool animationChanged = next.LapTenths != style.LapTenths || next.LineLength != style.LineLength;
        style = next;
        RefreshGeometry();
        if (animationChanged) RefreshEdge();
    }

    internal void SetExpanded(bool value)
    {
        if (expanded == value) return;
        expanded = value;
        RefreshEdge();
    }

    private void RefreshColors()
    {
        bool contrast = accessibility.HighContrast;
        var foreground = contrast ? ui.GetColorValue(UIColorType.Foreground) : Microsoft.UI.Colors.White;
        plate.BorderBrush = new SolidColorBrush(contrast ? foreground : Color.FromArgb(142, 116, 195, 255));
        plate.Background = contrast ? new SolidColorBrush(ui.GetColorValue(UIColorType.Background)) : BluePlate();
        var brush = new SolidColorBrush(foreground);
        foreach (var square in squares) square.Background = brush;
        if (stroke is not null) stroke.Color = foreground;
    }

    private static LinearGradientBrush BluePlate()
    {
        var brush = new LinearGradientBrush { StartPoint = new(0.16, 0), EndPoint = new(.76, 1) };
        brush.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(255, 10, 171, 255) });
        brush.GradientStops.Add(new GradientStop { Offset = .44, Color = Color.FromArgb(255, 6, 120, 236) });
        brush.GradientStops.Add(new GradientStop { Offset = .9, Color = Color.FromArgb(255, 6, 73, 213) });
        return brush;
    }

    private void RefreshGeometry()
    {
        plate.CornerRadius = new CornerRadius(size * .24);
        double group = size * style.Size / 100d;
        double gap = group * style.Gap / 100d;
        double square = (group - gap) / 2d;
        marks.Width = marks.Height = group;
        marks.RowSpacing = marks.ColumnSpacing = gap;
        foreach (var mark in squares) mark.CornerRadius = new CornerRadius(square * style.Radius / 100d);
        if (edge is null) return;
        visual!.Size = new Vector2(size);
        edge.Size = new Vector2(size - 1);
        edge.CornerRadius = new Vector2(size * .24f - .5f);
    }

    private void RefreshEdge()
    {
        if (visual is not null) visual.IsVisible = expanded && loaded;
        edge?.StopAnimation(nameof(CompositionGeometry.TrimOffset));
        if (!expanded || !loaded) return;
        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        if (edge is null)
        {
            edge = compositor.CreateRoundedRectangleGeometry();
            edge.Offset = new Vector2(.5f);
            var line = compositor.CreateSpriteShape(edge);
            stroke = compositor.CreateColorBrush();
            line.StrokeBrush = stroke;
            line.StrokeThickness = 1;
            visual = compositor.CreateShapeVisual();
            visual.Shapes.Add(line);
            ElementCompositionPreview.SetElementChildVisual(this, visual);
            RefreshGeometry();
            RefreshColors();
        }
        edge.TrimOffset = 0;
        edge.TrimEnd = style.LineLength / 100f;
        if (style.LineLength == 100 || !ui.AnimationsEnabled) return;
        using var animation = compositor.CreateScalarKeyFrameAnimation();
        using var linear = compositor.CreateLinearEasingFunction();
        animation.InsertKeyFrame(0, 0, linear);
        animation.InsertKeyFrame(1, 1, linear);
        animation.Duration = TimeSpan.FromMilliseconds(style.LapTenths * 100);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        edge.StartAnimation(nameof(CompositionGeometry.TrimOffset), animation);
    }
}
