using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Halo;

internal sealed partial class SettingsPage
{
    readonly ComboBox dragMode = new() { Header = "拖动预览位置" };
    readonly Slider dragDistance = new() { Minimum = 0, Maximum = DragPreviewPlacement.MaximumDistance, StepFrequency = 1 };
    readonly TextBlock dragDistanceLabel = new();
    readonly Canvas dragSample = new() { Height = 122, MaxWidth = 300, HorizontalAlignment = HorizontalAlignment.Stretch, IsHitTestVisible = false };
    readonly Border dragSampleCard = StationWindow.CreateDragPreviewCard(new Grid { Width = 36, Height = 36 });
    readonly Polygon dragSamplePointer = new() {
        Points = new PointCollection { new(0, 0), new(0, 19), new(5, 15), new(9, 23), new(13, 21), new(9, 13), new(15, 13) },
        Fill = new SolidColorBrush(Colors.Black), Stroke = new SolidColorBrush(Colors.White), StrokeThickness = 1.2,
    };

    void BuildDragPreviewSettings(StackPanel body)
    {
        foreach (var (mode, label) in new[] {
            (DragPreviewMode.GrabPoint, "按下点跟随"), (DragPreviewMode.Below, "鼠标正下方"), (DragPreviewMode.LowerRight, "鼠标右下方"),
        }) dragMode.Items.Add(new ComboBoxItem { Content = label, Tag = mode });
        dragMode.SelectedIndex = 0;
        body.Children.Add(dragMode);
        body.Children.Add(dragDistanceLabel);
        body.Children.Add(dragDistance);
        AutomationProperties.SetName(dragDistance, "缩略图与指针的距离");

        ((Grid)dragSampleCard.Child).Children.Add(PanelTheme.Glyph("\uE8B7", 25));
        dragSample.Children.Add(dragSampleCard);
        dragSample.Children.Add(dragSamplePointer);
        body.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(ColorHelper.FromArgb(34, 100, 150, 170)), Child = dragSample });
        dragSample.SizeChanged += (_, _) => UpdateDragPreviewSample();
        dragMode.SelectionChanged += (_, _) => DragPreviewChanged();
        dragDistance.ValueChanged += (_, _) => DragPreviewChanged();
        UpdateDragPreviewSample();
    }

    DragPreviewMode SelectedDragMode => dragMode.SelectedItem is ComboBoxItem { Tag: DragPreviewMode mode } ? mode : DragPreviewMode.GrabPoint;

    void RestoreDragPreviewSettings(HaloSettings settings)
    {
        dragMode.SelectedItem = dragMode.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, settings.DragPreviewMode));
        dragDistance.Value = settings.DragPreviewDistance;
        UpdateDragPreviewSample();
    }

    void DragPreviewChanged()
    {
        if (loading) return;
        UpdateDragPreviewSample();
        UpdateSaveState();
    }

    void UpdateDragPreviewSample()
    {
        var placement = new DragPreviewPlacement(SelectedDragMode, (int)dragDistance.Value);
        dragDistance.IsEnabled = placement.Mode != DragPreviewMode.GrabPoint;
        dragDistanceLabel.Text = $"与指针距离  {placement.Distance} DIP";
        const double imageSize = 48;
        double x = Math.Clamp(dragSample.ActualWidth / 2, 24, Math.Max(24, dragSample.ActualWidth - imageSize - DragPreviewPlacement.MaximumDistance));
        const double y = 46;
        var offset = placement.Offset(imageSize, imageSize, new Point(.35, .35));
        Canvas.SetLeft(dragSamplePointer, x); Canvas.SetTop(dragSamplePointer, y);
        Canvas.SetLeft(dragSampleCard, x + offset.X); Canvas.SetTop(dragSampleCard, y + offset.Y);
    }

    internal ComboBox DragModeForVerification => dragMode;
    internal Slider DragDistanceForVerification => dragDistance;
    internal Point DragSampleOffsetForVerification => new(Canvas.GetLeft(dragSampleCard) - Canvas.GetLeft(dragSamplePointer),
        Canvas.GetTop(dragSampleCard) - Canvas.GetTop(dragSamplePointer));
}
