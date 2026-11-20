using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Halo;

internal sealed class SelectionNameOverlay
{
    readonly TextBlock selectionName = new() {
        TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontSize = 12,
        LineHeight = 15, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
    };
    readonly Canvas selectionNameLayer = new() { IsHitTestVisible = false };
    readonly Microsoft.UI.Xaml.Shapes.Path selectionNameExtension = new() { Visibility = Visibility.Collapsed };
    readonly Border selectionNameSurface = new() {
        Visibility = Visibility.Collapsed, Padding = new Thickness(3, 0, 3, 2), CornerRadius = new CornerRadius(4),
    };
    TextBlock? selectedCompactName;
    (Point Label, Point Viewport, double LabelWidth, double ViewWidth, double ViewHeight)? positionedName;

    readonly GridView files;
    readonly Func<bool> suppressed;
    internal SelectionNameOverlay(Grid root, GridView files, FrameworkElement above, Func<bool> suppressed)
    {
        this.files = files;
        this.suppressed = suppressed;
        selectionNameSurface.Child = selectionName;
        selectionNameLayer.Children.Add(selectionNameExtension);
        selectionNameLayer.Children.Add(selectionNameSurface);
        Grid.SetRow(selectionNameLayer, Grid.GetRow(files));
        AutomationProperties.SetAccessibilityView(selectionNameLayer, AccessibilityView.Raw);
        root.Children.Insert(root.Children.IndexOf(above), selectionNameLayer);
        files.SelectionChanged += (_, _) => UpdateSelectionDetails();
        root.LayoutUpdated += (_, _) => PositionSelectionName();
    }

    internal void ApplyColors(StationInteractionColors interactionColors)
    {
        selectionNameExtension.Fill = new SolidColorBrush(interactionColors.Selection);
        selectionName.Foreground = new SolidColorBrush(interactionColors.NameText);
    }

    static TextBlock CompactName(GridViewItem card) => (TextBlock)((StackPanel)((ContentControl)card.Content).Content).Children[1];

    internal void UpdateSelectionDetails()
    {
        if (selectedCompactName != null) selectedCompactName.Opacity = 1;
        selectedCompactName = null;
        positionedName = null;
        selectionNameSurface.Visibility = Visibility.Collapsed;
        selectionNameExtension.Visibility = Visibility.Collapsed;
        var selection = files.SelectedItems;
        if (suppressed() || selection.Count != 1 ||
            selection[0] is not GridViewItem { Tag: StationItem { Kind: StationKind.File or StationKind.Folder } item } card)
            return;
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(item.Value));
        selectionName.Text = name.Length > 0 ? name : item.Value;
        selectedCompactName = CompactName(card);
        PositionSelectionName();
    }

    void PositionSelectionName()
    {
        var label = selectedCompactName;
        if (label == null || !label.IsLoaded || selectionNameLayer.XamlRoot == null || label.ActualWidth <= 0
            || files.ActualHeight <= 0 || files.SelectedItems.FirstOrDefault() is not GridViewItem card)
            return;

        var origin = label.TransformToVisual(selectionNameLayer).TransformPoint(new());
        var viewport = files.TransformToVisual(selectionNameLayer).TransformPoint(new());
        var layout = (origin, viewport, label.ActualWidth, files.ActualWidth, files.ActualHeight);
        if (positionedName == layout) return;
        positionedName = layout;
        double bottom = viewport.Y + files.ActualHeight;
        double right = viewport.X + files.ActualWidth;
        if (origin.Y < viewport.Y || origin.Y >= bottom || origin.X + label.ActualWidth <= viewport.X || origin.X >= right) {
            label.Opacity = 1;
            selectionNameSurface.Visibility = Visibility.Collapsed;
            selectionNameExtension.Visibility = Visibility.Collapsed;
            return;
        }

        double availableWidth = right - viewport.X;
        double width = Math.Min(availableWidth, label.ActualWidth + 6);
        selectionName.FontSize = 12;
        selectionName.LineHeight = 15;
        selectionName.Measure(new Size(Math.Max(1, width - 6), double.PositiveInfinity));
        if (selectionName.DesiredSize.Height + 2 > bottom - origin.Y) {
            // Give long names near the last row more horizontal room before raising the label.
            width = Math.Min(availableWidth, Math.Max(width, 240));
            selectionName.Measure(new Size(Math.Max(1, width - 6), double.PositiveInfinity));
        }
        if (selectionName.DesiredSize.Height + 2 > files.ActualHeight) {
            width = availableWidth;
            selectionName.Measure(new Size(Math.Max(1, width - 6), double.PositiveInfinity));
            if (selectionName.DesiredSize.Height + 2 > files.ActualHeight) {
                selectionName.FontSize = 10;
                selectionName.LineHeight = 13;
                selectionName.Measure(new Size(Math.Max(1, width - 6), double.PositiveInfinity));
            }
        }
        double x = Math.Clamp(origin.X + (label.ActualWidth - width) / 2, viewport.X, Math.Max(viewport.X, right - width));
        double y = Math.Clamp(origin.Y, viewport.Y, Math.Max(viewport.Y, bottom - selectionName.DesiredSize.Height - 2));
        if (selectionNameSurface.Width != width) selectionNameSurface.Width = width;
        if (Canvas.GetLeft(selectionNameSurface) != x) Canvas.SetLeft(selectionNameSurface, x);
        if (Canvas.GetTop(selectionNameSurface) != y) Canvas.SetTop(selectionNameSurface, y);
        var cardOrigin = card.TransformToVisual(selectionNameLayer).TransformPoint(new Point());
        double height = selectionName.DesiredSize.Height + 2;
        var all = new Rect(x, y, width, height);
        double left = Math.Max(x, cardOrigin.X), top = Math.Max(y, cardOrigin.Y);
        double insideRight = Math.Min(x + width, cardOrigin.X + card.ActualWidth);
        double insideBottom = Math.Min(y + height, cardOrigin.Y + card.ActualHeight);
        if (left > x || top > y || insideRight < x + width || insideBottom < y + height) {
            var extension = new GeometryGroup { FillRule = FillRule.EvenOdd };
            extension.Children.Add(new RectangleGeometry { Rect = all });
            if (insideRight > left && insideBottom > top)
                extension.Children.Add(new RectangleGeometry { Rect = new Rect(left, top, insideRight - left, insideBottom - top) });
            selectionNameExtension.Data = extension;
            selectionNameExtension.Visibility = Visibility.Visible;
        } else selectionNameExtension.Visibility = Visibility.Collapsed;
        label.Opacity = 0;
        selectionNameSurface.Visibility = Visibility.Visible;
    }

    internal void VerifySelectionDetails(Grid root, Action<string, bool> check)
    {
        var previous = files.SelectedItems.ToArray();
        var cards = files.Items.Cast<GridViewItem>().Where(card => ((StationItem)card.Tag).Kind is StationKind.File or StationKind.Folder)
            .OrderByDescending(card => Path.GetFileName(((StationItem)card.Tag).Value).Length).Take(2).ToArray();
        var label = CompactName(cards[0]);
        var bounds = (cards[0].ActualWidth, cards[0].ActualHeight);
        var cell = StationGridPanel.Cell(cards[0]);
        files.SelectedItems.Clear();
        files.SelectedItems.Add(cards[0]);
        root.UpdateLayout();
        check("SingleSelectionDisplaysFullFileNameAtCard", selectionName.Text == Path.GetFileName(((StationItem)cards[0].Tag).Value)
            && selectionNameSurface.Visibility == Visibility.Visible && label.Opacity == 0 && !selectionNameLayer.IsHitTestVisible);
        check("ExpandedFileNameKeepsGridGeometry", bounds == (cards[0].ActualWidth, cards[0].ActualHeight)
            && cell == StationGridPanel.Cell(cards[0]) && root.RowDefinitions.Count == 2);
        check("LongFileNameWrapsPastCompactLabel", selectionName.ActualHeight > label.ActualHeight
            && selectionName.MaxLines == 0 && selectionName.TextTrimming == TextTrimming.None);
        var presenter = StationWindow.Child<Microsoft.UI.Xaml.Controls.Primitives.ListViewItemPresenter>(cards[0]);
        check("ExpandedNameContinuesSelectedCardFill",
            selectionNameExtension.Visibility == Visibility.Visible && selectionNameSurface.Background == null
            && selectionNameExtension.Fill is SolidColorBrush extension
            && presenter?.SelectedBackground is SolidColorBrush fill && fill.Color == extension.Color
            && presenter.SelectedPointerOverBackground is SolidColorBrush hover && hover.Color == extension.Color
            && presenter.SelectedPressedBackground is SolidColorBrush pressed && pressed.Color == extension.Color);
        files.SelectedItems.Add(cards[1]);
        check("MultipleSelectionKeepsCompactFileNames", selectionNameSurface.Visibility == Visibility.Collapsed && label.Opacity == 1);
        files.SelectedItems.Clear();
        check("ClearedSelectionHidesName", selectionNameSurface.Visibility == Visibility.Collapsed && selectedCompactName == null);
        foreach (var card in previous) files.SelectedItems.Add(card);
    }
}

internal sealed partial class StationWindow
{
    SelectionNameOverlay selectionDetails = null!;

    void InitializeSelectionDetails() => selectionDetails = new(root, files, statusHost,
        () => destroyed || ShowingSettings || Searching || App.Station.LibraryOffline || DragActive || outgoingHandoff);
    void ApplySelectionNameColors() => selectionDetails.ApplyColors(interactionColors);
    void UpdateSelectionDetails() => selectionDetails.UpdateSelectionDetails();
    internal void VerifySelectionDetails(Action<string, bool> check) => selectionDetails.VerifySelectionDetails(root, check);
}
