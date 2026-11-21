using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.ViewManagement;
using Native = AIEnter.Native;

namespace AITaskbar;

internal sealed class WindowThumbnailPopup : Window
{
    private const int Padding = 8, Gap = 6, CardHeight = 124, ImageHeight = 86, PagingHeight = 34;
    private readonly Canvas panel = new();
    private readonly Action<RunningWindow> activate;
    private readonly List<nint> thumbnails = [];
    private readonly nint handle;
    private RunningWindow[] windows = [];
    private RectInt32 anchor, workArea;
    private double scale;
    private int columns, cardWidth, pageSize, page;
    private bool visible;

    public WindowThumbnailPopup(Action<RunningWindow> activate, Action enter, Action leave)
    {
        this.activate = activate;
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

    public void Open(nint owner, FrameworkElement button, IEnumerable<RunningWindow> sources)
    {
        windows = sources.Where(RunningWindows.IsCurrent).ToArray();
        if (windows.Length == 0 || button.XamlRoot is not { } root) { Dismiss(); return; }
        var point = button.TransformToVisual(root.Content).TransformPoint(new());
        scale = root.RasterizationScale;
        var origin = new Native.Point { X = Pixel(point.X), Y = Pixel(point.Y) };
        if (!ClientToScreen(owner, ref origin)) { Dismiss(); return; }
        anchor = new(origin.X, origin.Y, Pixel(button.ActualWidth), Pixel(button.ActualHeight));
        workArea = DisplayArea.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(owner),
            DisplayAreaFallback.Nearest).WorkArea;
        int available = (int)(workArea.Width / scale) - 2 * Padding;
        if (available < 80) { Dismiss(); return; }
        cardWidth = Math.Min(168, available);
        columns = Math.Min(windows.Length, Math.Clamp((available + Gap) / (cardWidth + Gap), 1, 5));
        pageSize = columns;
        page = 0;
        panel.RequestedTheme = button.ActualTheme;
        var system = new UISettings();
        bool contrast = new AccessibilitySettings().HighContrast;
        panel.Background = new SolidColorBrush(contrast ? system.GetColorValue(UIColorType.Background)
            : button.ActualTheme == ElementTheme.Dark ? Color.FromArgb(255, 34, 38, 45)
                : Color.FromArgb(255, 243, 247, 251));
        RenderPage();
    }

    public void Dismiss()
    {
        ClearThumbnails();
        if (!visible) return;
        visible = false;
        AppWindow.Hide();
    }

    private void RenderPage()
    {
        ClearThumbnails();
        panel.Children.Clear();
        bool paging = windows.Length > pageSize;
        int visibleCount = Math.Min(pageSize, windows.Length - page * pageSize);
        int width = 2 * Padding + columns * cardWidth + (columns - 1) * Gap;
        int height = 2 * Padding + CardHeight + (paging ? PagingHeight : 0);
        int widthPx = Pixel(width), heightPx = Pixel(height);
        int x = Math.Clamp(anchor.X + (anchor.Width - widthPx) / 2, workArea.X, workArea.X + workArea.Width - widthPx);
        int y = Math.Max(workArea.Y, anchor.Y - heightPx - Pixel(10));
        AppWindow.MoveAndResize(new(x, y, widthPx, heightPx));
        for (int index = 0; index < visibleCount; index++)
        {
            var source = windows[page * pageSize + index];
            int left = Padding + index * (cardWidth + Gap);
            var at = new RectInt32(left + 6, Padding + 6, cardWidth - 12, ImageHeight);
            AddCard(source, left, at);
        }
        if (paging)
        {
            var navigation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Gap };
            navigation.Children.Add(PageButton("‹", "上一页", -1, page > 0));
            navigation.Children.Add(new TextBlock { Text = $"{page + 1} / {(windows.Length + pageSize - 1) / pageSize}",
                VerticalAlignment = VerticalAlignment.Center });
            navigation.Children.Add(PageButton("›", "下一页", 1, (page + 1) * pageSize < windows.Length));
            Canvas.SetLeft(navigation, Padding);
            Canvas.SetTop(navigation, Padding + CardHeight + 4);
            panel.Children.Add(navigation);
        }
        if (!visible)
        {
            visible = true;
            AppWindow.Show(activateWindow: false);
        }
    }

    private void AddCard(RunningWindow source, int left, RectInt32 at)
    {
        var placeholder = new TextBlock { Text = "无可用缩略图", Opacity = .65,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = DisplayTitle(source.Title), TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12,
            Margin = new Thickness(6, 0, 6, 5), VerticalAlignment = VerticalAlignment.Bottom };
        var content = new Grid { Width = cardWidth, Height = CardHeight };
        var image = new Grid { Width = at.Width, Height = at.Height, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 0, 0) };
        image.Children.Add(placeholder);
        content.Children.Add(image);
        content.Children.Add(title);
        var button = new Button { Width = cardWidth, Height = CardHeight, Padding = new Thickness(0),
            BorderThickness = new Thickness(0), Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch };
        AutomationProperties.SetName(button, "切换到 " + source.Title);
        ToolTipService.SetToolTip(button, source.Title);
        button.Click += (_, _) => activate(source);
        Canvas.SetLeft(button, left);
        Canvas.SetTop(button, Padding);
        panel.Children.Add(button);
        if (Register(source, at)) placeholder.Visibility = Visibility.Collapsed;
    }

    private static string DisplayTitle(string title)
    {
        // Explorer can show the full folder path in its title bar.
        return Path.IsPathFullyQualified(title) && Path.GetFileName(title) is { Length: > 0 } name ? name : title;
    }

    private Button PageButton(string glyph, string name, int direction, bool enabled)
    {
        var button = new Button { Content = glyph, IsEnabled = enabled, Padding = new Thickness(9, 2, 9, 2) };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => { page += direction; RenderPage(); };
        return button;
    }

    private bool Register(RunningWindow source, RectInt32 area)
    {
        if (!RunningWindows.IsCurrent(source) || DwmRegisterThumbnail(handle, source.Handle, out nint thumbnail) < 0) return false;
        if (DwmQueryThumbnailSourceSize(thumbnail, out var size) >= 0 && size.Width > 0 && size.Height > 0)
        {
            double fit = Math.Min((double)area.Width / size.Width, (double)area.Height / size.Height);
            double width = size.Width * fit, height = size.Height * fit;
            double left = area.X + (area.Width - width) / 2, top = area.Y + (area.Height - height) / 2;
            var properties = new ThumbnailProperties
            {
                Flags = 1 | 8, Visible = 1, Destination = new Native.Rect
                { Left = Pixel(left), Top = Pixel(top), Right = Pixel(left + width), Bottom = Pixel(top + height) }
            };
            if (DwmUpdateThumbnailProperties(thumbnail, in properties) >= 0)
            {
                thumbnails.Add(thumbnail);
                return true;
            }
        }
        DwmUnregisterThumbnail(thumbnail);
        return false;
    }

    private int Pixel(double dip) => (int)Math.Round(dip * scale);

    private void ClearThumbnails()
    {
        foreach (var thumbnail in thumbnails) DwmUnregisterThumbnail(thumbnail);
        thumbnails.Clear();
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
