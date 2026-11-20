using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace Halo;

internal sealed record LibraryDrag(LibraryLocation Source, StationItem[] Items, Guid Anchor)
{
    internal string Token { get; } = Guid.NewGuid().ToString("N");
    internal bool HandledInside { get; set; }
}

internal sealed record LibraryDropSpot(LibraryLocation Destination, FrameworkElement Element, bool IsBreadcrumb)
{
    internal string Caption => IsBreadcrumb ? "松开以移动到这里" : "松开以移入文件夹";
    internal string CopyCaption => IsBreadcrumb ? "松开以复制到这里" : "松开以复制到此文件夹";
}

internal sealed class LibraryDropCue
{
    readonly Canvas layer = new() { IsHitTestVisible = false };
    readonly Border hint = new() {
        CornerRadius = new CornerRadius(8), Visibility = Visibility.Collapsed, IsHitTestVisible = false,
    };
    readonly TextBlock label = new() {
        TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Padding = new Thickness(3), FontSize = 11,
    };
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer timer;
    LibraryDropSpot? candidate;
    long started;
    bool copying;
    internal bool HasCandidate => candidate != null;
    internal LibraryDropSpot? Armed { get; private set; }

    internal LibraryDropCue(Grid root, Action armed)
    {
        hint.Child = label;
        layer.Children.Add(hint);
        Grid.SetRowSpan(layer, root.RowDefinitions.Count);
        root.Children.Add(layer);
        timer = root.DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = FolderDropIntent.Delay;
        timer.Tick += (_, _) => {
            if (candidate is not { Element.IsLoaded: true }) { Clear(); return; }
            var elapsed = Stopwatch.GetElapsedTime(started);
            if (elapsed < FolderDropIntent.Delay) {
                timer.Interval = FolderDropIntent.Delay - elapsed;
                timer.Start();
                return;
            }
            Armed = candidate;
            Draw(Armed);
            armed();
        };
    }

    internal void ApplyColors(StationInteractionColors colors)
    {
        hint.Background = new SolidColorBrush(colors.Drag);
        hint.BorderBrush = new SolidColorBrush(colors.Outline);
        hint.BorderThickness = new Thickness(1);
        label.Foreground = new SolidColorBrush(PanelTheme.ReadableText(colors.Drag,
            layer.ActualTheme == ElementTheme.Dark));
    }

    internal bool Track(LibraryDropSpot? spot, bool copy = false)
    {
        copying = copy;
        if (spot != candidate) {
            Clear();
            candidate = spot;
            if (spot is { IsBreadcrumb: true }) Armed = spot;
            else if (spot != null) {
                started = Stopwatch.GetTimestamp();
                timer.Interval = FolderDropIntent.Delay;
                timer.Start();
            }
        }
        if (Armed != null) Draw(Armed);
        return spot != null;
    }

    internal LibraryLocation? Release(LibraryDropSpot? current) =>
        Armed != null && Armed == current ? Armed.Destination : null;

    internal void Clear()
    {
        timer.Stop();
        Armed = candidate = null;
        hint.Visibility = Visibility.Collapsed;
    }

    void Draw(LibraryDropSpot spot)
    {
        if (!layer.IsLoaded || !spot.Element.IsLoaded || layer.XamlRoot != spot.Element.XamlRoot) return;
        var bounds = Bounds(spot.Element, layer);
        var visible = FolderDropIntent.Intersect(bounds, new(0, 0, layer.ActualWidth, layer.ActualHeight));
        Canvas.SetLeft(hint, visible.Left);
        Canvas.SetTop(hint, visible.Top);
        hint.Width = visible.Width;
        hint.Height = visible.Height;
        label.Text = copying ? spot.CopyCaption : spot.Caption;
        label.Visibility = spot.IsBreadcrumb ? Visibility.Collapsed : Visibility.Visible;
        hint.Visibility = visible.Width > 0 && visible.Height > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    internal static Rect Bounds(FrameworkElement element, UIElement relativeTo)
    {
        var origin = element.TransformToVisual(relativeTo).TransformPoint(new());
        return new(origin.X, origin.Y, element.ActualWidth, element.ActualHeight);
    }

    internal static bool HitsFolderCenter(Rect bounds, Point point)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return false;
        return new Rect(bounds.X + bounds.Width * .15, bounds.Y + bounds.Height * .15,
            bounds.Width * .7, bounds.Height * .7).Contains(point);
    }

    internal static bool IsGridBlank(GridView files, Point point) =>
        new Rect(0, 0, files.ActualWidth, files.ActualHeight).Contains(point)
        && !files.Items.Cast<GridViewItem>().Any(card => card.IsLoaded && Bounds(card, files).Contains(point));

    internal static LibraryDropSpot? FolderAt(GridView files, Point point, LibraryLocation source,
        StationItem[] items, Func<StationItem, LibraryLocation> location, StationStore store)
    {
        if (store.ReadOnly || !StationStore.CanMoveLibraryItems(items)
            || !new Rect(0, 0, files.ActualWidth, files.ActualHeight).Contains(point)) return null;
        foreach (GridViewItem card in files.Items) {
            if (!card.IsLoaded || card.Tag is not StationItem { Kind: StationKind.Folder, Owned: true } folder
                || !HitsFolderCenter(Bounds(card, files), point)) continue;
            var destination = location(folder);
            if (StationStore.CanMoveTo(source, destination, items)) return new(destination, card, false);
        }
        return null;
    }
}

internal sealed partial class StationWindow
{
    const string LibraryDragFormat = "Halo.InternalDrag.1";
    internal LibraryDrag? ActiveNativeLibraryDrag { get; private set; }

    internal static bool HasLibraryDragIdentity(DataPackageView view) => view.Contains(LibraryDragFormat);

    internal LibraryDrag BeginNativeLibraryDrag(DataPackage data, LibraryLocation source, StationItem[] items, Guid anchor)
    {
        var drag = new LibraryDrag(source, items, anchor);
        data.SetData(LibraryDragFormat, drag.Token);
        ActiveNativeLibraryDrag = drag;
        return drag;
    }

    internal async Task<bool> MatchesNativeLibraryDrag(DataPackageView view, LibraryDrag drag) =>
        ReferenceEquals(ActiveNativeLibraryDrag, drag) && HasLibraryDragIdentity(view)
        && await view.GetDataAsync(LibraryDragFormat) is string token && token == drag.Token
        && ReferenceEquals(ActiveNativeLibraryDrag, drag);

    internal void EndNativeLibraryDrag(LibraryDrag? drag)
    {
        if (ReferenceEquals(ActiveNativeLibraryDrag, drag)) ActiveNativeLibraryDrag = null;
    }

    internal static bool SameLibraryLocation(LibraryLocation a, LibraryLocation b) => a.RootId == b.RootId
        && string.Equals(a.Directory, b.Directory, StringComparison.OrdinalIgnoreCase);
}
