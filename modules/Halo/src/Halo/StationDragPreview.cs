using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Halo;

internal sealed partial class StationWindow
{
    readonly Canvas dragPreviewLayer = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    readonly TranslateTransform dragPreviewPosition = new();
    readonly Dictionary<GridViewItem, double> dragSourceOpacity = [];
    internal const double DragPreviewFramePadding = 6;
    Point dragPreviewAnchor, dragPreviewGrab;
    DragPreviewPlacement dragPreviewPlacement;
    internal sealed record DragPreviewPixels(IBuffer Buffer, int Width, int Height, Point Anchor);
    Task<DragPreviewPixels?>? dragPreviewCapture;
    SoftwareBitmap? outgoingPreview;

    void InitializeDragPreview()
    {
        Grid.SetRowSpan(dragPreviewLayer, root.RowDefinitions.Count);
        AutomationProperties.SetAccessibilityView(dragPreviewLayer, AccessibilityView.Raw);
        root.Children.Insert(root.Children.IndexOf(insertion), dragPreviewLayer);
        Closed += (_, _) => HideDragPreview();
    }

    void ShowDragPreview(Preview source, Point position, int count, Point grab)
    {
        var size = GridAppearance.IconSize;
        var card = CreateDragThumbnail(source, size, count, interactionColors);
        foreach (var selected in SelectedCards()) {
            dragSourceOpacity.Add(selected, selected.Opacity);
            selected.Opacity *= .5;
        }
        dragPreviewPlacement = DragPreviewPlacement.From(App.Settings.Data);
        dragPreviewGrab = grab;
        var (anchor, padding) = dragPreviewPlacement.Layout(size + 2 * DragPreviewFramePadding, grab);
        dragPreviewAnchor = anchor;
        var visual = new Border { Child = card, Padding = padding, RenderTransform = dragPreviewPosition };
        dragPreviewLayer.Children.Add(visual);
        MoveDragPreview(position);
        dragPreviewLayer.Visibility = Visibility.Visible;
        dragPreviewCapture = CaptureDragPreview(visual, anchor);
        UpdateSelectionDetails();
    }

    internal static Border CreateDragThumbnail(Preview source, int size, int count, StationInteractionColors colors)
    {
        var thumbnail = new Grid { Width = size, Height = size };
        if (source.Image.Source is { } image)
            thumbnail.Children.Add(new Image { Source = image, Stretch = Stretch.Uniform });
        else
            thumbnail.Children.Add(PanelTheme.Glyph(source.Fallback.Glyph, size * .72));

        var card = CreateDragPreviewCard(thumbnail);
        ApplyDragPreviewColors(card, colors);
        if (count > 1)
        {
            var badge = new Border {
                Background = new SolidColorBrush(ColorHelper.FromArgb(255, 33, 87, 134)),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(5, 1, 5, 1),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Child = new TextBlock { Text = count.ToString(), FontSize = 11, Foreground = new SolidColorBrush(Colors.White) },
            };
            thumbnail.Children.Add(badge);
        }
        return card;
    }

    internal static Border CreateDragPreviewCard(FrameworkElement thumbnail) => new() {
        Child = thumbnail, CornerRadius = new CornerRadius(8),
    };

    internal static void ApplyDragPreviewColors(Border card, StationInteractionColors colors)
    {
        colors.ApplyBorder(card, colors.Drag, 1);
        card.Padding = new Thickness(DragPreviewFramePadding - card.BorderThickness.Left);
    }

    void MoveDragPreview(Point position)
    {
        dragPreviewPosition.X = position.X - dragPreviewAnchor.X;
        dragPreviewPosition.Y = position.Y - dragPreviewAnchor.Y;
    }

    Rect? DragPreviewFrameAt(Point point)
    {
        if (dragPreviewLayer.Visibility != Visibility.Visible && !outgoingHandoff) return null;
        double size = GridAppearance.IconSize + 2 * DragPreviewFramePadding;
        // Offset describes the painted thumbnail frame and excludes the transparent hotspot padding.
        var offset = dragPreviewPlacement.Offset(size, size, dragPreviewGrab);
        return new Rect(point.X + offset.X, point.Y + offset.Y, size, size);
    }

    void HideDragPreview()
    {
        dragPreviewCapture = null;
        dragPreviewLayer.Visibility = Visibility.Collapsed;
        if (dragPreviewLayer.Children.Count == 0) return;
        dragPreviewLayer.Children.Clear();
        foreach (var (card, opacity) in dragSourceOpacity) card.Opacity = opacity;
        dragSourceOpacity.Clear();
    }

    internal static async Task<DragPreviewPixels?> CaptureDragPreview(FrameworkElement card, Point anchor)
    {
        try {
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(card);
            if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0) return null;
            var scale = card.XamlRoot?.RasterizationScale ?? 1;
            return new(await bitmap.GetPixelsAsync(), bitmap.PixelWidth, bitmap.PixelHeight,
                new(Math.Min(anchor.X * scale, bitmap.PixelWidth - 1), Math.Min(anchor.Y * scale, bitmap.PixelHeight - 1)));
        }
        catch (Exception error) { App.Log(error); return null; }
    }

    internal static SoftwareBitmap? CreateDragBitmap(Task<DragPreviewPixels?>? capture)
    {
        if (capture is not { IsCompletedSuccessfully: true } || capture.Result is not { } pixels) return null;
        try {
            return SoftwareBitmap.CreateCopyFromBuffer(pixels.Buffer, BitmapPixelFormat.Bgra8,
                pixels.Width, pixels.Height, BitmapAlphaMode.Premultiplied);
        }
        catch (Exception error) { App.Log(error); return null; }
    }

    async Task ApplyOutgoingDragPreview(DragUI ui, Preview source)
    {
        if (!outgoingHandoff) return;
        try {
            var capture = dragPreviewCapture;
            // Keep the themed visual attached until WinUI has captured it, including fast edge crossings.
            if (capture is { IsCompleted: false })
                await Task.WhenAny(capture, Task.Delay(100));
            outgoingPreview = CreateDragBitmap(capture);
            if (outgoingPreview != null)
                ui.SetContentFromSoftwareBitmap(outgoingPreview, capture!.Result!.Anchor);
            else if (source.Image.Source is BitmapImage { PixelWidth: > 0 } thumbnail) {
                var scale = root.XamlRoot?.RasterizationScale ?? 1;
                var offset = dragPreviewPlacement.Offset(thumbnail.PixelWidth, thumbnail.PixelHeight, dragPreviewGrab, scale);
                ui.SetContentFromBitmapImage(thumbnail, new(-offset.X, -offset.Y));
            }
        }
        catch (Exception error) { App.Log(error); }
        finally { HideDragPreview(); }
    }

    internal async Task VerifyDragPreview(Action<string, bool> check)
    {
        var selected = files.SelectedItems.ToArray();
        var card = files.Items.Cast<GridViewItem>().First();
        var grip = (ContentControl)card.Content;
        var originalOpacity = card.Opacity;
        string catalog = Path.Combine(App.DataDirectory, "station.json");
        string before = await File.ReadAllTextAsync(catalog);
        SelectCard(card, false, false);
        cardPress = new(card, 1, ReleaseSelection.None);
        pointerDragLease = KeepOpenForDrag();
        movingIds.Add(((StationItem)card.Tag).Id);
        try {
            ShowDragPreview((Preview)grip.Tag, new(60, 70), 1, new(.25, .5));
            UpdateDropTarget(new(50, 60));
            MoveDragPreview(new(90, 110));
            check("DragThumbnailFollowsPointerWithBlueTarget",
                dragPreviewLayer.Visibility == Visibility.Visible && !dragPreviewLayer.IsHitTestVisible
                && dragPreviewPosition.X + dragPreviewAnchor.X == 90 && dragPreviewPosition.Y + dragPreviewAnchor.Y == 110
                && card.Opacity < originalOpacity && insertion.Visibility == Visibility.Visible);
            var capture = dragPreviewCapture!;
            var preview = (Border)((Border)dragPreviewLayer.Children[0]).Child;
            check("DragPreviewUsesButtonPaletteAndOptionalBorder",
                preview.Background is SolidColorBrush background && background.Color == interactionColors.Drag
                && preview.BorderBrush is SolidColorBrush outline && outline.Color == interactionColors.Outline
                && preview.BorderThickness == new Thickness(interactionColors.ShowBorders ? 1 : 0)
                && preview.Padding.Left + preview.BorderThickness.Left == DragPreviewFramePadding);
            var pixels = await capture;
            using var nativeBitmap = CreateDragBitmap(capture);
            check("DragUICanUseSameCardPixels", nativeBitmap is { PixelWidth: > 0, PixelHeight: > 0, BitmapAlphaMode: BitmapAlphaMode.Premultiplied }
                && pixels != null && pixels.Buffer.Length == nativeBitmap.PixelWidth * nativeBitmap.PixelHeight * 4);
            using var reader = DataReader.FromBuffer(pixels!.Buffer);
            var bgra = new byte[pixels.Buffer.Length];
            reader.ReadBytes(bgra);
            check("DragUICardHasVisibleTranslucentPixels", bgra.Where((_, index) => index % 4 == 3).Any(alpha => alpha is > 0 and < 255));
            var scale = root.XamlRoot.RasterizationScale;
            check("DragUISharesPointerAnchor", pixels.Anchor.Equals(new Point(dragPreviewAnchor.X * scale, dragPreviewAnchor.Y * scale)));
            var grab = new Point(.25, .5);
            var grabbed = new DragPreviewPlacement(DragPreviewMode.GrabPoint, 6).Layout(48, grab);
            var below = new DragPreviewPlacement(DragPreviewMode.Below, 14).Layout(48, grab);
            var right = new DragPreviewPlacement(DragPreviewMode.LowerRight, 20).Layout(48, grab);
            check("DragPreviewModesUseGrabOrConfiguredDistance", grabbed.Anchor.Equals(new Point(12, 24)) && grabbed.Padding.Top == 0
                && below.Anchor.Equals(new Point(24, 0)) && below.Padding.Top == 14 && below.Padding.Left == 0
                && right.Anchor.Equals(new Point()) && right.Padding.Left == 20 && right.Padding.Top == 20);
            check("DragPreviewDoesNotWriteLayout", await File.ReadAllTextAsync(catalog) == before);
        }
        finally {
            CancelPointerDrag();
            files.SelectedItems.Clear();
            foreach (var item in selected) files.SelectedItems.Add(item);
        }
        check("CancelDragClearsPreviewAndPreservesLayout", dragPreviewLayer.Visibility == Visibility.Collapsed
            && insertion.Visibility == Visibility.Collapsed && card.Opacity == originalOpacity
            && await File.ReadAllTextAsync(catalog) == before);
    }
}
