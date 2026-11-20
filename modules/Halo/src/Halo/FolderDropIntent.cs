using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Halo;

internal sealed class FolderDropIntent
{
    internal const string Caption = "松开以创建文件夹";
    internal static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(300);

    readonly Func<GridViewItem?> target;
    readonly Action<GridViewItem> draw;
    GridViewItem? armedCard;
    double opacity;
    long started;

    internal Guid? Candidate { get; private set; }
    internal Guid? Armed { get; private set; }
    internal DispatcherQueueTimer Timer { get; }
    internal StackPanel Cue { get; } = new() {
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed, Spacing = 2, Padding = new Thickness(3),
    };

    internal FolderDropIntent(DispatcherQueue dispatcher, Border frame, Func<GridViewItem?> target, Action<GridViewItem> draw)
    {
        this.target = target;
        this.draw = draw;
        Cue.Children.Add(PanelTheme.Glyph("\uE8B7", 23));
        Cue.Children.Add(new TextBlock {
            Text = Caption, FontSize = 11, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
        });
        frame.Child = Cue;
        Timer = dispatcher.CreateTimer();
        Timer.IsRepeating = false;
        Timer.Interval = Delay;
        Timer.Tick += (_, _) => {
            if (Armed.HasValue) return;
            var card = target();
            if (Candidate is not { } id || card?.Tag is not StationItem item || item.Id != id) { Reset(); return; }
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            if (elapsed < Delay) { Timer.Interval = Delay - elapsed; Timer.Start(); return; }
            Armed = id;
            armedCard = card;
            opacity = card.Opacity;
            card.Opacity = 0;
            draw(card);
            Cue.Visibility = Visibility.Visible;
        };
    }

    internal bool Track(GridViewItem? card)
    {
        var id = (card?.Tag as StationItem)?.Id;
        if (id != Candidate) {
            Reset();
            if (id != null) {
                Candidate = id;
                started = System.Diagnostics.Stopwatch.GetTimestamp();
                Timer.Interval = Delay;
                Timer.Start();
            }
        }
        if (card == null) return false;
        draw(card);
        return true;
    }

    internal Guid? OnRelease(GridViewItem? card) =>
        Armed is { } id && card?.Tag is StationItem item && item.Id == id ? id : null;

    internal void Reset()
    {
        Timer.Stop();
        Candidate = Armed = null;
        if (armedCard != null) armedCard.Opacity = opacity;
        armedCard = null;
        Cue.Visibility = Visibility.Collapsed;
    }

    internal static Rect Intersect(Rect a, Rect b)
    {
        double x = Math.Max(a.X, b.X), y = Math.Max(a.Y, b.Y);
        return new Rect(x, y, Math.Max(0, Math.Min(a.Right, b.Right) - x), Math.Max(0, Math.Min(a.Bottom, b.Bottom) - y));
    }

    internal static bool OverlapsMostOfPreview(Rect preview, Rect target)
    {
        var overlap = Intersect(preview, target);
        return overlap.Width * overlap.Height > preview.Width * preview.Height / 2;
    }

    internal static Rect CardFrame(GridView files, GridViewItem card)
    {
        var origin = card.TransformToVisual(files).TransformPoint(new());
        return new Rect(origin.X, origin.Y, card.ActualWidth, card.ActualHeight);
    }

    internal static GridViewItem? FindTarget(GridView files, Rect preview, Guid source, Func<Point, (int Column, int Row)> slotAt)
    {
        if (files.ItemsPanelRoot is not StationGridPanel panel) return null;
        var viewport = new Rect(0, 0, files.ActualWidth, files.ActualHeight);
        var visible = Intersect(preview, viewport);
        if (visible.Width == 0 || visible.Height == 0) return null;
        var first = slotAt(new(visible.X, visible.Y));
        var last = slotAt(new(visible.Right, visible.Bottom));
        foreach (GridViewItem card in files.Items) {
            int cell = StationGridPanel.Cell(card);
            int column = cell % panel.Columns, row = cell / panel.Columns;
            if (column < first.Column || column > last.Column || row < first.Row || row > last.Row
                || !card.IsLoaded || card.Tag is not StationItem item || item.Id == source || !StationStore.CanGroupFile(item))
                continue;
            if (OverlapsMostOfPreview(preview, Intersect(CardFrame(files, card), viewport))) return card;
        }
        return null;
    }
}
