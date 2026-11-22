using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using Native = AIEnter.Native;

namespace AITaskbar;

internal sealed class WindowThumbnailPopup : Window
{
    private const int Padding = WindowThumbnailLayout.Padding, Gap = WindowThumbnailLayout.Gap;
    private const uint Destination = 1, VisibilityFlag = 8;
    private readonly Canvas panel = new();
    private Action<RunningWindow>? activate, close;
    private readonly nint handle;
    private readonly List<Source> windows = [];
    private WindowThumbnailLayout.Page[] pages = [];
    private RectInt32 anchor, workArea;
    private double scale;
    private int page, requestedWidth;
    private bool visible, above;
    private sealed record Source(RunningWindow Window, nint Thumbnail, double Aspect);

    public WindowThumbnailPopup(Action enter, Action leave)
    {
        Title = "AITaskbar 窗口缩略图";
        AppWindow.IsShownInSwitchers = false;
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = presenter.IsMinimizable = presenter.IsMaximizable = false;
        presenter.IsAlwaysOnTop = true;
        handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Native.RemoveWindowFrame(handle);
        panel.PointerEntered += (_, _) => enter();
        panel.PointerExited += (_, _) => leave();
        Content = panel;
        Closed += (_, _) => ClearThumbnails();
    }

    public void Open(nint owner, FrameworkElement button, IEnumerable<RunningWindow> sources, int width,
        Action<RunningWindow> activate, Action<RunningWindow> close)
    {
        ClearThumbnails();
        if (button.XamlRoot is not { } root) { Dismiss(); return; }
        var point = button.TransformToVisual(root.Content).TransformPoint(new());
        scale = root.RasterizationScale;
        var origin = new Native.Point { X = Pixel(point.X), Y = Pixel(point.Y) };
        if (!ClientToScreen(owner, ref origin)) { Dismiss(); return; }
        anchor = new(origin.X, origin.Y, Pixel(button.ActualWidth), Pixel(button.ActualHeight));
        workArea = DisplayArea.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(owner),
            DisplayAreaFallback.Nearest).WorkArea;
        foreach (var source in sources.Where(RunningWindows.IsCurrent)) windows.Add(Register(source));
        if (windows.Count == 0) { Dismiss(); return; }
        this.activate = activate;
        this.close = close;
        requestedWidth = width;
        page = 0;
        RebuildPages();
    }

    internal void SetTheme(ElementTheme theme, Color background)
    {
        panel.RequestedTheme = theme;
        if (panel.Background is not SolidColorBrush brush || brush.Color != background)
            panel.Background = new SolidColorBrush(background);
    }

    internal void Resize(int width)
    {
        if (requestedWidth == width) return;
        requestedWidth = width;
        if (visible) RebuildPages(pages[page].Start);
    }

    private void RebuildPages(int first = 0)
    {
        int gap = Pixel(WindowThumbnailLayout.AnchorGap);
        int roomAbove = Math.Min(anchor.Y, workArea.Y + workArea.Height) - workArea.Y - gap;
        int roomBelow = workArea.Y + workArea.Height - Math.Max(anchor.Y + anchor.Height, workArea.Y) - gap;
        above = roomAbove >= roomBelow;
        pages = WindowThumbnailLayout.Build(windows.Select(source => source.Aspect).ToArray(), requestedWidth,
            (int)Math.Floor(workArea.Width / scale), (int)Math.Floor(Math.Max(roomAbove, roomBelow) / scale));
        if (pages.Length == 0) { Dismiss(); return; }
        page = Math.Max(0, Array.FindLastIndex(pages, value => value.Start <= first));
        RenderPage();
    }

    public void Dismiss()
    {
        ClearThumbnails();
        activate = close = null;
        if (!visible) return;
        visible = false;
        AppWindow.Hide();
    }

    private void RenderPage()
    {
        foreach (var source in windows)
            if (source.Thumbnail != 0)
            {
                var hide = new ThumbnailProperties { Flags = VisibilityFlag };
                DwmUpdateThumbnailProperties(source.Thumbnail, in hide);
            }
        panel.Children.Clear();
        var layout = pages[page];
        bool paging = pages.Length > 1;
        int width = layout.Width, height = layout.Height(paging);
        int widthPx = Pixel(width), heightPx = Pixel(height);
        int x = Math.Clamp(anchor.X + (anchor.Width - widthPx) / 2, workArea.X, workArea.X + workArea.Width - widthPx);
        int gap = Pixel(WindowThumbnailLayout.AnchorGap);
        int y = above ? anchor.Y - heightPx - gap : anchor.Y + anchor.Height + gap;
        y = Math.Clamp(y, workArea.Y, workArea.Y + workArea.Height - heightPx);
        AppWindow.MoveAndResize(new(x, y, widthPx, heightPx));
        int left = Padding;
        for (int index = 0; index < layout.Cards.Length; index++)
        {
            var card = layout.Cards[index];
            var image = new RectInt32(left + (card.Width - card.ImageWidth) / 2, 2 * Padding, card.ImageWidth, layout.ImageHeight);
            AddCard(windows[layout.Start + index], left, card.Width, layout.CardHeight, image);
            left += card.Width + Gap;
        }
        if (paging)
        {
            int navigationWidth = Math.Min(106, width - 2 * Padding);
            var navigation = new Grid { Width = navigationWidth, ColumnSpacing = 3 };
            navigation.ColumnDefinitions.Add(new() { Width = new GridLength(24) });
            navigation.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            navigation.ColumnDefinitions.Add(new() { Width = new GridLength(24) });
            navigation.Children.Add(PageButton("上一页", -1, page > 0));
            var count = new TextBlock { Text = $"{page + 1} / {pages.Length}", FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetColumn(count, 1);
            navigation.Children.Add(count);
            var next = PageButton("下一页", 1, page + 1 < pages.Length);
            Grid.SetColumn(next, 2);
            navigation.Children.Add(next);
            double centeredOnAnchor = (anchor.X + anchor.Width / 2d - x) / scale - navigationWidth / 2d;
            Canvas.SetLeft(navigation, Math.Clamp(centeredOnAnchor, Padding, width - Padding - navigationWidth));
            Canvas.SetTop(navigation, Padding + layout.CardHeight + 3);
            panel.Children.Add(navigation);
        }
        if (!visible)
        {
            visible = true;
            AppWindow.Show(activateWindow: false);
        }
    }

    private void AddCard(Source entry, int left, int cardWidth, int cardHeight, RectInt32 at)
    {
        var source = entry.Window;
        var placeholder = new TextBlock { Text = "无可用缩略图", Opacity = .65,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = DisplayTitle(source.Title), TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center };
        var footer = new Grid { Margin = new Thickness(Padding, 0, Padding + 26, Padding),
            VerticalAlignment = VerticalAlignment.Bottom, Height = WindowThumbnailLayout.FooterHeight };
        footer.Children.Add(title);
        var content = new Grid { Width = cardWidth, Height = cardHeight };
        var image = new Grid { Width = at.Width, Height = at.Height, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, Padding, 0, 0) };
        image.Children.Add(placeholder);
        content.Children.Add(image);
        content.Children.Add(footer);
        var button = new Button { Width = cardWidth, Height = cardHeight, Padding = new Thickness(0),
            BorderThickness = new Thickness(0), Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch };
        AutomationProperties.SetName(button, "切换到 " + source.Title);
        ToolTipService.SetToolTip(button, source.Title);
        button.Click += (_, _) => activate?.Invoke(source);
        Canvas.SetLeft(button, left);
        Canvas.SetTop(button, Padding);
        panel.Children.Add(button);
        var closeButton = ThumbnailButton(CloseIcon());
        AutomationProperties.SetName(closeButton, "关闭 " + source.Title);
        ToolTipService.SetToolTip(closeButton, "关闭窗口");
        closeButton.Click += (_, _) => close?.Invoke(source);
        Canvas.SetLeft(closeButton, left + cardWidth - Padding - 24);
        Canvas.SetTop(closeButton, Padding + cardHeight - Padding - WindowThumbnailLayout.FooterHeight + 2);
        panel.Children.Add(closeButton);
        if (ShowThumbnail(entry, at)) placeholder.Visibility = Visibility.Collapsed;
    }

    private static string DisplayTitle(string title)
    {
        // Explorer can show the full folder path in its title bar.
        return Path.IsPathFullyQualified(title) && Path.GetFileName(title) is { Length: > 0 } name ? name : title;
    }

    private Button PageButton(string name, int direction, bool enabled)
    {
        Point[] outline = [new(0, 1), new(1, 0), new(5, 4), new(1, 8), new(0, 7), new(3, 4)];
        if (direction < 0)
            for (int index = 0; index < outline.Length; index++) outline[index] = new(5 - outline[index].X, outline[index].Y);
        var button = ThumbnailButton(CenteredIcon(5, outline));
        button.IsEnabled = enabled;
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => { page += direction; RenderPage(); };
        return button;
    }

    private static Button ThumbnailButton(IconElement icon) => new()
    {
        Content = icon, Width = 24, Height = 24, Padding = new Thickness(0),
        HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
    };

    internal static PathIcon CloseIcon() => CenteredIcon(8,
        [new(1, 0), new(4, 3), new(7, 0), new(8, 1), new(5, 4), new(8, 7),
         new(7, 8), new(4, 5), new(1, 8), new(0, 7), new(3, 4), new(0, 1)]);

    private static PathIcon CenteredIcon(int width, Point[] points)
    {
        var figure = new PathFigure { StartPoint = points[0], IsClosed = true };
        foreach (var point in points.Skip(1)) figure.Segments.Add(new LineSegment { Point = point });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return new PathIcon
        {
            Data = geometry, Width = width, Height = 8,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
    }

    private Source Register(RunningWindow source)
    {
        if (DwmRegisterThumbnail(handle, source.Handle, out nint thumbnail) >= 0)
        {
            if (DwmQueryThumbnailSourceSize(thumbnail, out var size) >= 0 && size.Width > 0 && size.Height > 0)
                return new(source, thumbnail, (double)size.Width / size.Height);
            DwmUnregisterThumbnail(thumbnail);
        }
        return new(source, 0, 16d / 10);
    }

    private bool ShowThumbnail(Source source, RectInt32 area)
    {
        if (source.Thumbnail == 0) return false;
        double width = Math.Min(area.Width, area.Height * source.Aspect);
        double height = width / source.Aspect;
        double left = area.X + (area.Width - width) / 2, top = area.Y + (area.Height - height) / 2;
        var properties = new ThumbnailProperties
        {
            Flags = Destination | VisibilityFlag, Visible = 1, Destination = new Native.Rect
            { Left = Pixel(left), Top = Pixel(top), Right = Pixel(left + width), Bottom = Pixel(top + height) }
        };
        return DwmUpdateThumbnailProperties(source.Thumbnail, in properties) >= 0;
    }

    private int Pixel(double dip) => (int)Math.Round(dip * scale);

    private void ClearThumbnails()
    {
        foreach (var source in windows)
            if (source.Thumbnail != 0) DwmUnregisterThumbnail(source.Thumbnail);
        windows.Clear();
        pages = [];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThumbnailProperties
    {
        public uint Flags;
        public Native.Rect Destination, Source;
        public byte Opacity;
        public int Visible, SourceClientAreaOnly;
    }

    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref Native.Point point);
    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmQueryThumbnailSourceSize(nint thumbnail, out SizeInt32 size);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(nint thumbnail, in ThumbnailProperties properties);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(nint thumbnail);
}
